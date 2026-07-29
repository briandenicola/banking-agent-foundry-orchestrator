from fastapi import FastAPI

from app.agents import get_agent_graph
from app.contracts import AgentName, AgentRequest, AgentResult

app = FastAPI(title="Banking Multi-Agent Service")


async def invoke(agent: AgentName, request: AgentRequest) -> AgentResult:
    graph = get_agent_graph(agent)
    state = await graph.ainvoke({"request": request, "result": None})
    return state["result"]


@app.get("/health")
def health():
    return {
        "status": "ok",
        "agents": [
            "workflow-planning",
            "transaction-explanation",
            "suspicious-activity",
            "dispute-planning",
        ],
    }


@app.post("/plan", response_model=AgentResult)
async def plan(payload: AgentRequest):
    return await invoke(AgentName.WORKFLOW_PLANNING, payload)


@app.post("/reason", response_model=AgentResult)
async def reason(payload: AgentRequest):
    return await invoke(AgentName.TRANSACTION_EXPLANATION, payload)


@app.post("/transaction-explanation", response_model=AgentResult)
async def transaction_explanation(payload: AgentRequest):
    return await invoke(AgentName.TRANSACTION_EXPLANATION, payload)


@app.post("/suspicious-activity", response_model=AgentResult)
async def suspicious_activity(payload: AgentRequest):
    return await invoke(AgentName.SUSPICIOUS_ACTIVITY, payload)


@app.post("/dispute", response_model=AgentResult)
async def dispute(payload: AgentRequest):
    return await invoke(AgentName.DISPUTE_PLANNING, payload)
