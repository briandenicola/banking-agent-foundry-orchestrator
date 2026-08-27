"""Transaction-explanation agent: a genuine multi-node LangGraph graph.

Topology::

    START -> extract_reference -> classify_status -> (conditional)
                                                     |-> explain_transaction -> END
                                                     |-> request_transaction_details -> END

The conditional edge exists to protect the agent's core constraint: explain
transactions using only supplied context, and never invent merchant or account
data. When the customer has not identified *which* transaction they mean, there
is nothing to explain, and a model asked to explain it anyway will fabricate a
merchant, amount, or date. The graph short circuits to an explicit request for
details instead.

Both terminal branches set ``requires_approval=False``. This agent is
informational by charter: it never modifies an account, so it must never ask the
orchestrator to open an approval gate.
"""

from __future__ import annotations

from typing import Literal, TypedDict

from langgraph.graph import END, START, StateGraph
from pydantic import BaseModel, Field

from app.contracts import CONTRACT_VERSION, AgentName, AgentRequest, AgentResult
from app.model import structured_step, tool_findings

AGENT = AgentName.TRANSACTION_EXPLANATION

# Any one of these pins the explanation to a specific transaction. Kept as data
# so the "can we explain this?" rule is inspectable and testable rather than
# buried in a prompt.
REFERENCE_FIELDS = ("merchant", "amount", "transaction_date", "descriptor")


class TransactionReference(BaseModel):
    """Which transaction the customer means, with unknowns left unknown."""

    merchant: str | None = Field(
        default=None, description="Merchant name, or null if the customer did not say."
    )
    amount: str | None = Field(
        default=None, description="Transaction amount, or null if the customer did not say."
    )
    transaction_date: str | None = Field(
        default=None, description="Transaction date, or null if the customer did not say."
    )
    descriptor: str | None = Field(
        default=None,
        description="Statement descriptor or card ending the customer quoted, or null.",
    )

    def identifying_details(self) -> list[str]:
        return [field for field in REFERENCE_FIELDS if getattr(self, field)]

    def has_identifying_detail(self) -> bool:
        return bool(self.identifying_details())


class StatusAssessment(BaseModel):
    """How the transaction behaves, which drives the explanation's shape."""

    status: Literal[
        "pending",
        "posted",
        "reversed",
        "recurring",
        "card_present",
        "card_not_present",
        "unknown",
    ]
    settles_automatically: bool = Field(
        default=False,
        description="True when the transaction resolves on its own without customer action.",
    )
    rationale: str


class ExplanationNarrative(BaseModel):
    """Customer-facing wording for a terminal node."""

    summary: str
    recommended_action: str
    evidence: list[str] = Field(default_factory=list)


class ExplanationState(TypedDict, total=False):
    """State carried between nodes.

    ``reference`` feeds ``assessment``, and both feed whichever terminal node
    the conditional edge selects.
    """

    request: AgentRequest
    reference: TransactionReference
    assessment: StatusAssessment
    used_fallback: bool
    result: AgentResult


REFERENCE_INSTRUCTIONS = """
You extract the identifying details of the transaction a customer is asking
about. Record only what the customer actually supplied; leave a field null when
they did not mention it. Never guess a merchant, amount, date, or descriptor,
and never substitute a plausible example for a missing value.
"""

STATUS_INSTRUCTIONS = """
You categorise a banking transaction from the details gathered so far.
Distinguish pending, posted, reversed, recurring, card-present, and
card-not-present activity. Use the unknown category when the supplied details do
not justify a more specific one, and state uncertainty rather than guessing.
"""

TOOL_INSTRUCTIONS = """
You are helping explain a retail banking transaction to the customer who made it.

Use the available tools only when they add something you cannot state reliably
on your own, such as arithmetic over a set of transactions.

Do not use tools to invent transaction data that was not provided. Do not take,
recommend, or commit to any account action. If no tool is useful, call none.
""".strip()

EXPLAIN_INSTRUCTIONS = """
You explain a banking transaction to a customer using only the supplied context.
Cover what the transaction is, why it appears as it does, and when it will
settle if that applies. Never invent merchant or account data, and state
uncertainty clearly. This agent is informational and must not execute or promise
any account action.
"""

DETAILS_INSTRUCTIONS = """
You ask a customer for the details needed to identify a transaction, because
they have not yet said which one they mean. Ask for the merchant name, amount,
approximate date, or statement descriptor. Do not speculate about which
transaction they might mean and do not describe any specific transaction.
"""


def _context(state: ExplanationState) -> str:
    lines: list[str] = []
    reference = state.get("reference")
    if reference is not None:
        lines.append(f"Transaction reference: {reference.model_dump_json()}")
    assessment = state.get("assessment")
    if assessment is not None:
        lines.append(f"Status assessment: {assessment.model_dump_json()}")
    return "\n".join(lines)


async def extract_reference(state: ExplanationState) -> dict:
    """Node 1: capture which transaction the customer means.

    The fallback deliberately returns an empty reference. Without a model there
    is no reliable way to pull a merchant or amount out of free text, and
    inventing one is the exact failure this agent must avoid, so an unidentified
    transaction correctly routes to the request-details branch.
    """
    reference = await structured_step(
        REFERENCE_INSTRUCTIONS, state["request"], TransactionReference
    )
    if reference is None:
        return {"reference": TransactionReference(), "used_fallback": True}
    return {"reference": reference}


async def classify_status(state: ExplanationState) -> dict:
    """Node 2: categorise the transaction using the extracted reference."""
    assessment = await structured_step(
        STATUS_INSTRUCTIONS,
        state["request"],
        StatusAssessment,
        step_context=_context(state),
    )
    if assessment is None:
        return {
            "assessment": StatusAssessment(
                status="unknown",
                settles_automatically=False,
                rationale=(
                    "Determined locally: no model was available to categorise the transaction."
                ),
            ),
            "used_fallback": True,
        }
    return {"assessment": assessment}


def route_on_identifiability(
    state: ExplanationState,
) -> Literal["explain_transaction", "request_transaction_details"]:
    """The conditional edge: explain only a transaction we can actually name.

    The decision reads the extracted data rather than asking the model whether
    it feels able to answer, so a confident model cannot talk its way into
    explaining a transaction the customer never identified.
    """
    reference = state.get("reference")
    if reference is None or not reference.has_identifying_detail():
        return "request_transaction_details"
    return "explain_transaction"


def _result(
    state: ExplanationState,
    narrative: ExplanationNarrative,
    intent: str,
    fallback: bool,
) -> AgentResult:
    """Assemble the terminal contract.

    ``requires_approval`` is hard-coded to False and ``next_step`` to
    ``respond_to_user``. This agent is informational by charter, so neither the
    model nor a later prompt edit can make it request an approval gate.
    """
    used_fallback = bool(state.get("used_fallback")) or fallback
    return AgentResult(
        agent=AGENT,
        trace_id=state["request"].trace_id,
        contract_version=CONTRACT_VERSION,
        execution_mode="fallback" if used_fallback else "model",
        intent=intent,
        summary=narrative.summary,
        risk_level="low",
        requires_approval=False,
        recommended_action=narrative.recommended_action,
        next_step="respond_to_user",
        evidence=narrative.evidence,
    )


def _evidence(
    state: ExplanationState, narrative: ExplanationNarrative
) -> ExplanationNarrative:
    """Carry the identifying details into evidence when the model supplied none."""
    if narrative.evidence:
        return narrative
    reference = state.get("reference")
    if reference is None:
        return narrative
    details = [
        f"{field}: {getattr(reference, field)}"
        for field in reference.identifying_details()
    ]
    if not details:
        return narrative
    return narrative.model_copy(update={"evidence": details})


async def explain_transaction(state: ExplanationState) -> dict:
    """Terminal node 3a: the transaction is identified, so explain it."""
    # Toolbox tools (for example code interpreter for spending arithmetic) run
    # before the narrative so their observations become part of the context and
    # of the audit trail. This node is informational and cannot require
    # approval, so tool output can never influence an approval decision.
    findings = await tool_findings(TOOL_INSTRUCTIONS, state["request"])

    step_context = _context(state)
    if findings:
        step_context += "\n\nTool observations:\n" + "\n".join(findings)

    narrative = await structured_step(
        EXPLAIN_INSTRUCTIONS,
        state["request"],
        ExplanationNarrative,
        step_context=step_context,
    )
    fallback = narrative is None
    if narrative is None:
        narrative = ExplanationNarrative(
            summary="The request asks for an informational explanation of a transaction.",
            recommended_action=(
                "Explain the transaction status, merchant, timing, and likely causes."
            ),
            evidence=[],
        )

    narrative = _evidence(state, narrative)
    if findings:
        narrative = narrative.model_copy(
            update={"evidence": list(narrative.evidence) + findings}
        )

    return {
        "result": _result(
            state,
            narrative,
            intent="transaction_explanation",
            fallback=fallback,
        )
    }


async def request_transaction_details(state: ExplanationState) -> dict:
    """Terminal node 3b: nothing identifies the transaction, so ask."""
    narrative = await structured_step(
        DETAILS_INSTRUCTIONS,
        state["request"],
        ExplanationNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = ExplanationNarrative(
            summary="The customer has not identified which transaction they are asking about.",
            recommended_action=(
                "Ask for the merchant name, amount, approximate date, or statement descriptor."
            ),
            evidence=[],
        )

    return {
        "result": _result(
            state,
            narrative,
            intent="transaction_information_required",
            fallback=fallback,
        )
    }


def build_graph():
    graph = StateGraph(ExplanationState)
    graph.add_node("extract_reference", extract_reference)
    graph.add_node("classify_status", classify_status)
    graph.add_node("explain_transaction", explain_transaction)
    graph.add_node("request_transaction_details", request_transaction_details)

    graph.add_edge(START, "extract_reference")
    graph.add_edge("extract_reference", "classify_status")
    graph.add_conditional_edges(
        "classify_status",
        route_on_identifiability,
        {
            "explain_transaction": "explain_transaction",
            "request_transaction_details": "request_transaction_details",
        },
    )
    graph.add_edge("explain_transaction", END)
    graph.add_edge("request_transaction_details", END)
    return graph.compile()


graph = build_graph()
