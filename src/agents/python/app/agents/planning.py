"""Workflow-planning agent: a genuine multi-node LangGraph graph.

Topology::

    START -> interpret_request -> select_specialist -> (conditional)
                                                       |-> gate_action_request -> END
                                                       |-> route_informational -> END

The planner never executes anything; it classifies intent, picks exactly one
specialist, and states whether the workflow needs a human approval gate.
Splitting those concerns into separate nodes lets each safety rule be enforced
in code rather than argued for in a prompt:

``select_specialist`` refuses to route to dispute-planning unless the customer
explicitly asked for one. Unrecognised activity alone is suspicious-activity,
which can still recommend a dispute without committing the customer to one.
Routing to dispute by inference is the failure this downgrade prevents.

The conditional edge decides the approval gate from the resolved selection and a
deterministic keyword check, so a model that overlooks "freeze my card" cannot
route the request past the gate. Risk level and approval stay independent: a
message may be high risk and still need no approval.
"""

from __future__ import annotations

from typing import Literal, TypedDict

from langgraph.graph import END, START, StateGraph
from pydantic import BaseModel, Field

from app.contracts import CONTRACT_VERSION, AgentName, AgentRequest, AgentResult
from app.model import structured_step

AGENT = AgentName.WORKFLOW_PLANNING

# Verbs that mean "change my account". Kept as data so the approval rule is
# inspectable and testable rather than buried in a prompt. A miss here would
# silently skip the approval gate, so these are OR-ed with the model's judgement.
ACTION_TERMS = ("freeze", "block", "close", "lock", "cancel my card", "suspend")

# Language that constitutes actually asking for a dispute. These are AND-ed with
# the model's judgement: routing to dispute that the customer never requested is
# the failure mode, so both signals must agree before dispute is selected.
DISPUTE_TERMS = (
    "dispute",
    "chargeback",
    "charge back",
    "charge-back",
    "reverse",
    "reversal",
    "refund",
    "money back",
)

SPECIALISTS = {
    "transaction-explanation": AgentName.TRANSACTION_EXPLANATION,
    "suspicious-activity": AgentName.SUSPICIOUS_ACTIVITY,
    "dispute-planning": AgentName.DISPUTE_PLANNING,
}


class RequestInterpretation(BaseModel):
    """What the customer is actually asking us to do."""

    asks_to_act: bool = Field(
        default=False,
        description=(
            "True only if the customer asked to change the account or move money: "
            "freeze, block, close, lock, or suspend an account or card."
        ),
    )
    explicitly_requests_dispute: bool = Field(
        default=False,
        description=(
            "True only if the customer explicitly asked to dispute, contest, charge back, "
            "reverse, or be refunded for a specific charge. Never infer this from a "
            "transaction merely being unfamiliar or unrecognised."
        ),
    )
    reports_unrecognized_activity: bool = Field(
        default=False,
        description="True if the customer reports or suspects activity they do not recognise.",
    )
    rationale: str = ""


class SpecialistSelection(BaseModel):
    """Which specialist should handle the request, and how risky it looks."""

    selected_agent: Literal[
        "transaction-explanation",
        "suspicious-activity",
        "dispute-planning",
    ]
    intent: Literal["transaction_explanation", "suspicious_activity", "dispute"]
    risk_level: Literal["low", "medium", "high"]
    summary: str
    rationale: str = ""


class PlanningState(TypedDict, total=False):
    """State carried between nodes.

    ``interpretation`` feeds ``selection``, and both feed whichever terminal node
    the conditional edge selects. ``downgraded`` records that an unrequested
    dispute route was rejected, so the reason reaches the audit trail.
    """

    request: AgentRequest
    interpretation: RequestInterpretation
    selection: SpecialistSelection
    downgraded: bool
    escalated: bool
    used_fallback: bool
    result: AgentResult


INTERPRET_INSTRUCTIONS = """
You determine what a banking customer is actually asking for. Route on the
request itself, not on how alarming the message sounds.

Set asks_to_act to true only when the customer asks you to carry out something
that changes the account or moves money, such as freezing, blocking, locking,
suspending, or closing an account or card. Requests for explanation, guidance,
or next steps never require approval, regardless of how high the risk level is.
Risk level and approval are independent: a message may be high risk and still
need no approval.

Set explicitly_requests_dispute to true only when the customer explicitly asks
to dispute, contest, charge back, reverse, or be refunded for a specific charge.
A dispute must be requested. Never infer one from a customer merely saying a
transaction is unfamiliar, unrecognised, or not theirs.

Set reports_unrecognized_activity to true when the customer reports or suspects
activity they do not recognise, or raises fraud.
"""

SELECT_INSTRUCTIONS = """
You select exactly one specialist agent to handle a banking request, using the
interpretation gathered so far.

transaction-explanation
    The customer wants to understand a transaction they recognize: why it is
    pending, what a merchant descriptor means, why a fee or amount differs, or
    when funds will settle. No unauthorized activity is alleged.

suspicious-activity
    The customer reports or suspects activity they do not recognize, or raises
    fraud, or asks what to check, review, or do next about it. This is the
    correct choice even when the customer states plainly that a transaction is
    not theirs, as long as they have not asked you to reverse or dispute the
    charge. Asking "what should I review?" is a request for guidance.

dispute-planning
    Choose this ONLY when the customer explicitly asks to dispute, contest,
    charge back, reverse, or be refunded for a specific charge. A dispute must
    be requested. Never infer one from a customer merely saying a transaction is
    unfamiliar, unrecognized, or not theirs; unrecognized activity alone is
    suspicious-activity. When you are unsure whether the customer wants a
    dispute or only wants to understand what happened, choose
    suspicious-activity, because it can still recommend a dispute without
    committing the customer to one.

Assess risk_level independently of whether approval is needed. A message may be
high risk and still require no approval.
"""

GATE_INSTRUCTIONS = """
You summarise a banking workflow that will require explicit human approval
before anything happens. State what the customer asked for and that the action
has NOT been taken yet. Never imply an account has been changed, a card frozen,
or a dispute filed.
"""

INFORMATIONAL_INSTRUCTIONS = """
You summarise a banking workflow that is informational and needs no approval,
because the customer asked to understand something or for guidance rather than
asking you to change the account. Do not imply any action will be taken on the
account.
"""


class PlanNarrative(BaseModel):
    """Customer-facing wording for a terminal node."""

    summary: str
    recommended_action: str
    evidence: list[str] = Field(default_factory=list)


def _detect(message: str, terms: tuple[str, ...]) -> bool:
    lowered = message.lower()
    return any(term in lowered for term in terms)


def _context(state: PlanningState) -> str:
    lines: list[str] = []
    interpretation = state.get("interpretation")
    if interpretation is not None:
        lines.append(f"Interpretation: {interpretation.model_dump_json()}")
    selection = state.get("selection")
    if selection is not None:
        lines.append(f"Selection: {selection.model_dump_json()}")
    return "\n".join(lines)


async def interpret_request(state: PlanningState) -> dict:
    """Node 1: establish what the customer is asking for.

    ``asks_to_act`` is OR-ed with a keyword check so a missed detection still
    reaches the approval gate. ``explicitly_requests_dispute`` is AND-ed with a
    keyword check so an inferred dispute cannot be manufactured by the model.
    """
    request = state["request"]
    keyword_action = _detect(request.message, ACTION_TERMS)
    keyword_dispute = _detect(request.message, DISPUTE_TERMS)

    interpretation = await structured_step(
        INTERPRET_INSTRUCTIONS, request, RequestInterpretation
    )
    if interpretation is None:
        return {
            "interpretation": RequestInterpretation(
                asks_to_act=keyword_action,
                explicitly_requests_dispute=keyword_dispute,
                reports_unrecognized_activity=_detect(
                    request.message,
                    ("not mine", "not my", "suspicious", "fraud", "don't recognize",
                     "do not recognize", "unrecognized", "unrecognised"),
                ),
                rationale="Determined locally: no model was available to interpret the request.",
            ),
            "used_fallback": True,
        }

    return {
        "interpretation": interpretation.model_copy(
            update={
                "asks_to_act": interpretation.asks_to_act or keyword_action,
                "explicitly_requests_dispute": (
                    interpretation.explicitly_requests_dispute and keyword_dispute
                ),
            }
        )
    }


async def select_specialist(state: PlanningState) -> dict:
    """Node 2: choose exactly one specialist, then enforce the dispute rule.

    A dispute route survives only when the customer actually asked for one.
    Otherwise the request is downgraded to suspicious-activity, which can still
    recommend a dispute without committing the customer to one.
    """
    interpretation = state["interpretation"]

    selection = await structured_step(
        SELECT_INSTRUCTIONS,
        state["request"],
        SpecialistSelection,
        step_context=_context(state),
    )
    used_fallback = False
    if selection is None:
        used_fallback = True
        if interpretation.explicitly_requests_dispute:
            selection = SpecialistSelection(
                selected_agent="dispute-planning",
                intent="dispute",
                risk_level="high",
                summary="The customer asked to dispute or reverse a specific charge.",
            )
        elif interpretation.reports_unrecognized_activity or interpretation.asks_to_act:
            selection = SpecialistSelection(
                selected_agent="suspicious-activity",
                intent="suspicious_activity",
                risk_level="high",
                summary=(
                    "The customer reported activity they do not recognise, or asked for a "
                    "protective action on the account."
                ),
            )
        else:
            selection = SpecialistSelection(
                selected_agent="transaction-explanation",
                intent="transaction_explanation",
                risk_level="low",
                summary="The customer asked to understand a transaction.",
            )

    downgraded = False
    if (
        selection.selected_agent == "dispute-planning"
        and not interpretation.explicitly_requests_dispute
    ):
        downgraded = True
        selection = selection.model_copy(
            update={
                "selected_agent": "suspicious-activity",
                "intent": "suspicious_activity",
            }
        )

    # transaction-explanation is informational by charter and hard-codes
    # requires_approval=False, so it can never handle a request to change the
    # account. Escalate rather than route an action to an agent that cannot gate it.
    escalated = False
    if selection.selected_agent == "transaction-explanation" and interpretation.asks_to_act:
        escalated = True
        selection = selection.model_copy(
            update={
                "selected_agent": "suspicious-activity",
                "intent": "suspicious_activity",
            }
        )

    update: dict = {
        "selection": selection,
        "downgraded": downgraded,
        "escalated": escalated,
    }
    if used_fallback:
        update["used_fallback"] = True
    return update


def route_on_action_gate(
    state: PlanningState,
) -> Literal["gate_action_request", "route_informational"]:
    """The conditional edge: acting needs approval, understanding does not.

    Dispute-planning always gates because initiating a dispute is action-taking
    even when the customer's wording is calm.
    """
    selection = state["selection"]
    interpretation = state["interpretation"]
    if selection.selected_agent == "dispute-planning":
        return "gate_action_request"
    return "gate_action_request" if interpretation.asks_to_act else "route_informational"


def _result(
    state: PlanningState,
    narrative: PlanNarrative,
    requires_approval: bool,
    fallback: bool,
) -> AgentResult:
    """Assemble the terminal contract.

    ``requires_approval`` comes from the graph edge and ``selected_agent`` from
    the resolved selection, so neither can be overridden by generated prose.
    ``next_step`` is always ``invoke_specialist``: the planner hands off, and the
    specialist re-decides the gate for itself.
    """
    selection = state["selection"]
    used_fallback = bool(state.get("used_fallback")) or fallback

    evidence = list(narrative.evidence)
    if state.get("downgraded"):
        evidence.append(
            "Routed to suspicious-activity instead of dispute-planning: the customer "
            "did not explicitly request a dispute."
        )
    if state.get("escalated"):
        evidence.append(
            "Routed to suspicious-activity instead of transaction-explanation: the "
            "customer asked for an action on the account, which requires approval."
        )

    return AgentResult(
        agent=AGENT,
        trace_id=state["request"].trace_id,
        contract_version=CONTRACT_VERSION,
        execution_mode="fallback" if used_fallback else "model",
        intent=selection.intent,
        summary=narrative.summary or selection.summary,
        risk_level=selection.risk_level,
        requires_approval=requires_approval,
        recommended_action=narrative.recommended_action,
        next_step="invoke_specialist",
        selected_agent=SPECIALISTS[selection.selected_agent],
        evidence=evidence,
    )


async def gate_action_request(state: PlanningState) -> dict:
    """Terminal node 3a: the workflow changes the account, so it must be gated."""
    narrative = await structured_step(
        GATE_INSTRUCTIONS,
        state["request"],
        PlanNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = PlanNarrative(
            summary=state["selection"].summary,
            recommended_action="Invoke the selected specialist agent and hold for approval.",
            evidence=[],
        )

    return {
        "result": _result(state, narrative, requires_approval=True, fallback=fallback)
    }


async def route_informational(state: PlanningState) -> dict:
    """Terminal node 3b: the customer asked to understand, so no gate."""
    narrative = await structured_step(
        INFORMATIONAL_INSTRUCTIONS,
        state["request"],
        PlanNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = PlanNarrative(
            summary=state["selection"].summary,
            recommended_action="Invoke the selected specialist agent.",
            evidence=[],
        )

    return {
        "result": _result(state, narrative, requires_approval=False, fallback=fallback)
    }


def build_graph():
    graph = StateGraph(PlanningState)
    graph.add_node("interpret_request", interpret_request)
    graph.add_node("select_specialist", select_specialist)
    graph.add_node("gate_action_request", gate_action_request)
    graph.add_node("route_informational", route_informational)

    graph.add_edge(START, "interpret_request")
    graph.add_edge("interpret_request", "select_specialist")
    graph.add_conditional_edges(
        "select_specialist",
        route_on_action_gate,
        {
            "gate_action_request": "gate_action_request",
            "route_informational": "route_informational",
        },
    )
    graph.add_edge("gate_action_request", END)
    graph.add_edge("route_informational", END)
    return graph.compile()


graph = build_graph()
