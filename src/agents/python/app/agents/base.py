from __future__ import annotations

from typing import TypedDict

from langgraph.graph import END, START, StateGraph

from app.contracts import AgentName, AgentRequest, AgentResult
from app.model import reason


class AgentState(TypedDict):
    request: AgentRequest
    result: AgentResult | None


def build_agent_graph(agent: AgentName, instructions: str):
    async def analyze(state: AgentState) -> dict[str, AgentResult]:
        result = await reason(agent, instructions, state["request"])
        return {"result": result}

    graph = StateGraph(AgentState)
    graph.add_node("analyze", analyze)
    graph.add_edge(START, "analyze")
    graph.add_edge("analyze", END)
    return graph.compile()
