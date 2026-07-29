from __future__ import annotations

import os

from azure.ai.agentserver.invocations import InvocationAgentServerHost
from starlette.requests import Request
from starlette.responses import JSONResponse

from app.agents import get_agent_graph
from app.contracts import AgentName, AgentRequest

agent_name = AgentName(os.environ.get("BANKING_AGENT_KIND", AgentName.WORKFLOW_PLANNING))
graph = get_agent_graph(agent_name)
app = InvocationAgentServerHost()


@app.invoke_handler
async def handle_invoke(request: Request):
    payload = AgentRequest.model_validate(await request.json())
    state = await graph.ainvoke({"request": payload, "result": None})
    return JSONResponse(state["result"].model_dump(mode="json"))


if __name__ == "__main__":
    app.run()
