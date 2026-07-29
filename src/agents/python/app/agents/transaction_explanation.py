from app.agents.base import build_agent_graph
from app.contracts import AgentName

graph = build_agent_graph(
    AgentName.TRANSACTION_EXPLANATION,
    """
    You explain banking transactions using only supplied context. Distinguish
    pending, posted, reversed, recurring, card-present, and card-not-present
    activity. State uncertainty clearly and never invent merchant or account data.
    This agent is informational and must not execute account actions.
    """,
)
