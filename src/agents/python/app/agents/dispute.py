"""Dispute-planning agent: a genuine multi-node LangGraph graph.

Topology::

    START -> extract_claim -> validate_completeness -> (conditional)
                                                       |-> request_more_info -> END
                                                       |-> assess_evidence -> draft_plan -> END

The conditional edge is the point of the graph: a dispute claim that is missing
the facts a bank needs cannot be assessed for evidence, so the graph short
circuits to an information request instead of inventing details.

Both terminal branches set ``requires_approval=True``. Preparing a dispute plan
is never the same as filing one, and the orchestrator must hold a human gate in
front of dispute initiation regardless of how complete the claim is.
"""

from __future__ import annotations

from typing import Literal, TypedDict

from langgraph.graph import END, START, StateGraph
from pydantic import BaseModel, Field

from app.contracts import CONTRACT_VERSION, AgentName, AgentRequest, AgentResult
from app.model import structured_step

AGENT = AgentName.DISPUTE_PLANNING

# The facts a card dispute cannot proceed without. Kept as data so the
# completeness rule is inspectable and testable rather than buried in a prompt.
REQUIRED_CLAIM_FIELDS = ("merchant", "amount", "transaction_date", "reason")


class DisputeClaim(BaseModel):
    """What the customer actually told us, with unknowns left unknown."""

    merchant: str | None = Field(
        default=None, description="Merchant name, or null if the customer did not say."
    )
    amount: str | None = Field(
        default=None, description="Disputed amount, or null if the customer did not say."
    )
    transaction_date: str | None = Field(
        default=None, description="Transaction date, or null if the customer did not say."
    )
    reason: str | None = Field(
        default=None,
        description="Why the customer is disputing, e.g. unauthorized, not received, duplicate.",
    )

    def missing_fields(self) -> list[str]:
        return [field for field in REQUIRED_CLAIM_FIELDS if not getattr(self, field)]


class CompletenessCheck(BaseModel):
    """Whether the claim can support an evidence assessment."""

    is_complete: bool
    missing_fields: list[str] = Field(default_factory=list)
    rationale: str


class EvidenceAssessment(BaseModel):
    """What the customer would need to supply for a viable dispute."""

    required_evidence: list[str] = Field(default_factory=list)
    eligibility_notes: str
    strength: Literal["weak", "moderate", "strong"]


class DisputeNarrative(BaseModel):
    """Customer-facing wording for a terminal node."""

    summary: str
    recommended_action: str
    evidence: list[str] = Field(default_factory=list)


class DisputeState(TypedDict, total=False):
    """State carried between nodes.

    Unlike the single-node wrapper, intermediate nodes write real state that
    later nodes read: ``claim`` feeds ``completeness``, which selects the
    branch, and ``assessment`` feeds the drafted plan.
    """

    request: AgentRequest
    claim: DisputeClaim
    completeness: CompletenessCheck
    assessment: EvidenceAssessment
    used_fallback: bool
    result: AgentResult


EXTRACT_INSTRUCTIONS = """
You extract the facts of a card-dispute claim from a customer message.
Record only what the customer actually stated. If the customer did not state a
field, return null for it. Never guess a merchant, amount, or date.
"""

COMPLETENESS_INSTRUCTIONS = f"""
You decide whether a dispute claim contains enough information to assess
evidence requirements. A claim is complete only when all of these are known:
{", ".join(REQUIRED_CLAIM_FIELDS)}.
List every field that is missing. Do not treat a guess as a known value.
"""

EVIDENCE_INSTRUCTIONS = """
You determine what evidence a customer must supply for a card dispute, and note
any eligibility concerns such as filing deadlines or merchant-resolution
requirements. Judge the strength of the claim as stated. Never conclude that the
dispute will succeed; you are preparing a plan, not adjudicating it.
"""

MORE_INFO_INSTRUCTIONS = """
You write a short, courteous request for the specific dispute details that are
missing. Ask only for what is missing. Do not speculate about the outcome, and
do not imply that a dispute has been filed.
"""

DRAFT_PLAN_INSTRUCTIONS = """
You draft a bounded dispute plan from an assessed claim. State what will be
filed and what the customer must provide. Never state that a dispute has been
submitted; submission always requires explicit human approval.
"""


def _context(state: DisputeState) -> str:
    """Render prior node output so each node sees the graph's progress."""
    lines: list[str] = []
    claim = state.get("claim")
    if claim is not None:
        lines.append(f"Extracted claim: {claim.model_dump_json()}")
    completeness = state.get("completeness")
    if completeness is not None:
        lines.append(f"Completeness: {completeness.model_dump_json()}")
    assessment = state.get("assessment")
    if assessment is not None:
        lines.append(f"Evidence assessment: {assessment.model_dump_json()}")
    return "\n".join(lines)


async def extract_claim(state: DisputeState) -> dict:
    """Node 1: pull structured claim facts out of free text."""
    claim = await structured_step(EXTRACT_INSTRUCTIONS, state["request"], DisputeClaim)
    if claim is None:
        return {"claim": DisputeClaim(), "used_fallback": True}
    return {"claim": claim}


async def validate_completeness(state: DisputeState) -> dict:
    """Node 2: decide whether the claim can support an evidence assessment.

    The model is asked for a rationale, but the ``is_complete`` decision is
    recomputed from the extracted claim so that a model cannot declare an empty
    claim complete and skip the information request.
    """
    claim = state["claim"]
    missing = claim.missing_fields()

    check = await structured_step(
        COMPLETENESS_INSTRUCTIONS,
        state["request"],
        CompletenessCheck,
        step_context=_context(state),
    )
    if check is None:
        return {
            "completeness": CompletenessCheck(
                is_complete=not missing,
                missing_fields=missing,
                rationale="Determined locally from the extracted claim fields.",
            ),
            "used_fallback": True,
        }

    return {
        "completeness": check.model_copy(
            update={"is_complete": not missing, "missing_fields": missing}
        )
    }


def route_on_completeness(state: DisputeState) -> Literal["assess_evidence", "request_more_info"]:
    """The conditional edge: complete claims are assessed, incomplete ones are not."""
    return "assess_evidence" if state["completeness"].is_complete else "request_more_info"


async def assess_evidence(state: DisputeState) -> dict:
    """Node 3a: only reachable for a complete claim."""
    assessment = await structured_step(
        EVIDENCE_INSTRUCTIONS,
        state["request"],
        EvidenceAssessment,
        step_context=_context(state),
    )
    if assessment is None:
        return {
            "assessment": EvidenceAssessment(
                required_evidence=[
                    "Transaction receipt or statement line",
                    "Record of any contact with the merchant",
                ],
                eligibility_notes=(
                    "Filing deadlines depend on card scheme rules and the transaction date."
                ),
                strength="moderate",
            ),
            "used_fallback": True,
        }
    return {"assessment": assessment}


def _result(
    state: DisputeState,
    narrative: DisputeNarrative,
    intent: str,
    next_step: str,
    fallback: bool,
) -> AgentResult:
    """Assemble the terminal contract.

    ``requires_approval`` and ``risk_level`` are runtime-owned rather than
    model-owned: dispute initiation is an action-taking workflow, so the human
    gate must not depend on model output.
    """
    used_fallback = bool(state.get("used_fallback")) or fallback
    return AgentResult(
        agent=AGENT,
        trace_id=state["request"].trace_id,
        contract_version=CONTRACT_VERSION,
        execution_mode="fallback" if used_fallback else "model",
        intent=intent,
        summary=narrative.summary,
        risk_level="high",
        requires_approval=True,
        recommended_action=narrative.recommended_action,
        next_step=next_step,
        evidence=narrative.evidence,
    )


async def request_more_info(state: DisputeState) -> dict:
    """Terminal node 3b: ask for the missing facts instead of assessing."""
    missing = state["completeness"].missing_fields
    narrative = await structured_step(
        MORE_INFO_INSTRUCTIONS,
        state["request"],
        DisputeNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = DisputeNarrative(
            summary=(
                "The dispute request is missing details required to prepare a plan: "
                f"{', '.join(missing)}."
            ),
            recommended_action=f"Ask the customer for: {', '.join(missing)}.",
            evidence=[f"Missing claim field: {field}" for field in missing],
        )

    return {
        "result": _result(
            state,
            narrative,
            intent="dispute_information_required",
            next_step="request_approval",
            fallback=fallback,
        )
    }


async def draft_plan(state: DisputeState) -> dict:
    """Terminal node 4: draft the bounded plan from the assessed claim."""
    assessment = state["assessment"]
    narrative = await structured_step(
        DRAFT_PLAN_INSTRUCTIONS,
        state["request"],
        DisputeNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = DisputeNarrative(
            summary="A bounded dispute plan was prepared from the customer's claim.",
            recommended_action=(
                "Collect the required evidence, then request approval to file the dispute."
            ),
            evidence=list(assessment.required_evidence),
        )

    return {
        "result": _result(
            state,
            narrative,
            intent="dispute",
            next_step="request_approval",
            fallback=fallback,
        )
    }


def build_graph():
    graph = StateGraph(DisputeState)
    graph.add_node("extract_claim", extract_claim)
    graph.add_node("validate_completeness", validate_completeness)
    graph.add_node("assess_evidence", assess_evidence)
    graph.add_node("request_more_info", request_more_info)
    graph.add_node("draft_plan", draft_plan)

    graph.add_edge(START, "extract_claim")
    graph.add_edge("extract_claim", "validate_completeness")
    graph.add_conditional_edges(
        "validate_completeness",
        route_on_completeness,
        {"assess_evidence": "assess_evidence", "request_more_info": "request_more_info"},
    )
    graph.add_edge("assess_evidence", "draft_plan")
    graph.add_edge("draft_plan", END)
    graph.add_edge("request_more_info", END)
    return graph.compile()


graph = build_graph()
