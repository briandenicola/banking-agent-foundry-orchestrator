from __future__ import annotations

from app.agents.dispute import graph as dispute_graph
from app.agents.planning import graph as planning_graph
from app.agents.suspicious_activity import graph as suspicious_activity_graph
from app.agents.transaction_explanation import graph as transaction_explanation_graph
from app.contracts import AgentName

_GRAPHS = {
    AgentName.WORKFLOW_PLANNING: planning_graph,
    AgentName.TRANSACTION_EXPLANATION: transaction_explanation_graph,
    AgentName.SUSPICIOUS_ACTIVITY: suspicious_activity_graph,
    AgentName.DISPUTE_PLANNING: dispute_graph,
}


def get_agent_graph(agent: AgentName):
    return _GRAPHS[agent]
