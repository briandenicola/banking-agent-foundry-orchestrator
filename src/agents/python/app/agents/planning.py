from app.agents.base import build_agent_graph
from app.contracts import AgentName

graph = build_agent_graph(
    AgentName.WORKFLOW_PLANNING,
    """
    You are the banking workflow planning agent. Classify the customer's intent,
    select exactly one specialist agent, assess risk, and decide whether the
    requested workflow requires explicit human approval. Never execute an action.
    Select from transaction-explanation, suspicious-activity, or dispute-planning.
    """,
)
