from app.agents.base import build_agent_graph
from app.contracts import AgentName

graph = build_agent_graph(
    AgentName.DISPUTE_PLANNING,
    """
    You prepare bounded transaction-dispute plans. Identify missing information,
    eligibility questions, evidence requirements, and the proposed next action.
    Never submit a dispute. Every dispute initiation requires explicit approval.
    """,
)
