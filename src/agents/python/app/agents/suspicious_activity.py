"""Suspicious-activity agent: a genuine multi-node LangGraph graph.

Topology::

    START -> gather_signals -> classify -> (conditional)
                                           |-> plan_protective_action -> END
                                           |-> explain_activity -> END

The branch keys on whether the customer asked us to *change* the account
(freeze, block, close) rather than on severity. That distinction is the whole
safety property of this agent: describing risk is informational and completes
immediately, while touching an account is an action that must be gated behind
human approval no matter how confident the model is.
"""

from __future__ import annotations

from typing import Literal, TypedDict

from langgraph.graph import END, START, StateGraph
from pydantic import BaseModel, Field

from app.contracts import CONTRACT_VERSION, AgentName, AgentRequest, AgentResult
from app.model import structured_step

AGENT = AgentName.SUSPICIOUS_ACTIVITY

# Verbs that turn an informational request into an account-modifying one. The
# model is asked the same question, but this list is the backstop: a missed
# detection here would silently skip the approval gate.
ACTION_TERMS = ("freeze", "block", "close", "cancel my card", "lock")


class SignalSet(BaseModel):
    """Observed facts kept separate from inference, per the agent's charter."""

    observed_facts: list[str] = Field(
        default_factory=list, description="Only what the customer actually stated."
    )
    hypotheses: list[str] = Field(
        default_factory=list, description="Possible explanations, clearly not asserted as fact."
    )
    action_requested: bool = Field(
        default=False,
        description="True only if the customer asked to freeze, block, close, or lock the account or card.",
    )


class ActivityClassification(BaseModel):
    """How the activity is categorised, and how bad it looks."""

    category: Literal[
        "unauthorized_charge",
        "account_takeover",
        "card_lost_or_stolen",
        "recognized_activity",
        "insufficient_information",
    ]
    severity: Literal["low", "medium", "high"]
    rationale: str


class ActivityNarrative(BaseModel):
    """Customer-facing wording for a terminal node."""

    summary: str
    recommended_action: str
    evidence: list[str] = Field(default_factory=list)


class SuspiciousState(TypedDict, total=False):
    """State carried between nodes.

    ``signals`` feeds ``classification``, and both feed whichever terminal node
    the conditional edge selects.
    """

    request: AgentRequest
    signals: SignalSet
    classification: ActivityClassification
    used_fallback: bool
    result: AgentResult


SIGNALS_INSTRUCTIONS = """
You extract risk signals from a customer's report of suspicious account
activity. Separate what the customer actually observed from what might explain
it; never present a hypothesis as an observed fact. Set action_requested to true
only when the customer explicitly asks to freeze, block, close, or lock an
account or card.
"""

CLASSIFY_INSTRUCTIONS = """
You categorise suspicious banking activity and judge its severity from the
signals gathered so far. If the customer has not supplied enough detail to
categorise the activity, say so with the insufficient_information category
rather than guessing.
"""

PROTECTIVE_INSTRUCTIONS = """
You describe the protective action the customer has asked for and what will
happen once it is approved. State clearly that the action has NOT yet been
taken and requires explicit human approval. Never claim an account has been
frozen, blocked, or closed.
"""

EXPLAIN_INSTRUCTIONS = """
You explain suspicious account activity to a customer and recommend protective
steps they can consider. This is informational: do not modify the account and do
not imply that any action has been taken on their behalf.
"""


def _detect_action_request(message: str) -> bool:
    lowered = message.lower()
    return any(term in lowered for term in ACTION_TERMS)


def _context(state: SuspiciousState) -> str:
    lines: list[str] = []
    signals = state.get("signals")
    if signals is not None:
        lines.append(f"Signals: {signals.model_dump_json()}")
    classification = state.get("classification")
    if classification is not None:
        lines.append(f"Classification: {classification.model_dump_json()}")
    return "\n".join(lines)


async def gather_signals(state: SuspiciousState) -> dict:
    """Node 1: separate observed facts from hypotheses.

    ``action_requested`` is OR-ed with a deterministic keyword check. A model
    that overlooks "freeze my card" must not be able to route the request away
    from the approval gate.
    """
    request = state["request"]
    keyword_action = _detect_action_request(request.message)

    signals = await structured_step(SIGNALS_INSTRUCTIONS, request, SignalSet)
    if signals is None:
        return {
            "signals": SignalSet(
                observed_facts=["Customer reported activity they did not recognise."],
                hypotheses=["The charge may be unauthorized, or an unfamiliar merchant name."],
                action_requested=keyword_action,
            ),
            "used_fallback": True,
        }

    return {
        "signals": signals.model_copy(
            update={"action_requested": signals.action_requested or keyword_action}
        )
    }


async def classify(state: SuspiciousState) -> dict:
    """Node 2: categorise the activity using the gathered signals."""
    classification = await structured_step(
        CLASSIFY_INSTRUCTIONS,
        state["request"],
        ActivityClassification,
        step_context=_context(state),
    )
    if classification is None:
        return {
            "classification": ActivityClassification(
                category="unauthorized_charge",
                severity="high",
                rationale="Determined locally: the customer reported unrecognised activity.",
            ),
            "used_fallback": True,
        }
    return {"classification": classification}


def route_on_action(state: SuspiciousState) -> Literal["plan_protective_action", "explain_activity"]:
    """The conditional edge: acting on the account is gated, explaining is not."""
    return "plan_protective_action" if state["signals"].action_requested else "explain_activity"


def _result(
    state: SuspiciousState,
    narrative: ActivityNarrative,
    intent: str,
    requires_approval: bool,
    next_step: str,
    fallback: bool,
) -> AgentResult:
    """Assemble the terminal contract.

    ``requires_approval`` is decided by the graph edge, not by the model, so the
    approval gate cannot be argued away in generated text.
    """
    used_fallback = bool(state.get("used_fallback")) or fallback
    classification = state.get("classification")
    return AgentResult(
        agent=AGENT,
        trace_id=state["request"].trace_id,
        contract_version=CONTRACT_VERSION,
        execution_mode="fallback" if used_fallback else "model",
        intent=intent,
        summary=narrative.summary,
        risk_level=classification.severity if classification is not None else "high",
        requires_approval=requires_approval,
        recommended_action=narrative.recommended_action,
        next_step=next_step,
        evidence=narrative.evidence,
    )


def _evidence(state: SuspiciousState, narrative: ActivityNarrative) -> ActivityNarrative:
    """Carry observed facts into evidence when the model supplied none."""
    if narrative.evidence:
        return narrative
    signals = state.get("signals")
    if signals is None:
        return narrative
    return narrative.model_copy(update={"evidence": list(signals.observed_facts)})


async def plan_protective_action(state: SuspiciousState) -> dict:
    """Terminal node 3a: the customer asked us to change the account."""
    narrative = await structured_step(
        PROTECTIVE_INSTRUCTIONS,
        state["request"],
        ActivityNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = ActivityNarrative(
            summary=(
                "The customer asked for a protective account action following suspicious activity."
            ),
            recommended_action=(
                "Request approval before applying the protective action to the account."
            ),
            evidence=[],
        )

    return {
        "result": _result(
            state,
            _evidence(state, narrative),
            intent="suspicious_activity",
            requires_approval=True,
            next_step="request_approval",
            fallback=fallback,
        )
    }


async def explain_activity(state: SuspiciousState) -> dict:
    """Terminal node 3b: informational, so no approval gate."""
    narrative = await structured_step(
        EXPLAIN_INSTRUCTIONS,
        state["request"],
        ActivityNarrative,
        step_context=_context(state),
    )
    fallback = narrative is None
    if narrative is None:
        narrative = ActivityNarrative(
            summary="The reported activity was reviewed and protective steps were suggested.",
            recommended_action=(
                "Review recent transactions and report any the customer does not recognise."
            ),
            evidence=[],
        )

    return {
        "result": _result(
            state,
            _evidence(state, narrative),
            intent="suspicious_activity",
            requires_approval=False,
            next_step="respond_to_user",
            fallback=fallback,
        )
    }


def build_graph():
    graph = StateGraph(SuspiciousState)
    graph.add_node("gather_signals", gather_signals)
    graph.add_node("classify", classify)
    graph.add_node("plan_protective_action", plan_protective_action)
    graph.add_node("explain_activity", explain_activity)

    graph.add_edge(START, "gather_signals")
    graph.add_edge("gather_signals", "classify")
    graph.add_conditional_edges(
        "classify",
        route_on_action,
        {
            "plan_protective_action": "plan_protective_action",
            "explain_activity": "explain_activity",
        },
    )
    graph.add_edge("plan_protective_action", END)
    graph.add_edge("explain_activity", END)
    return graph.compile()


graph = build_graph()
