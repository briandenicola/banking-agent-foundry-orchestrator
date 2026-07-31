from __future__ import annotations

import asyncio
import json
import logging
import os

from azure.ai.agentserver.invocations import InvocationAgentServerHost
from pydantic import ValidationError
from starlette.requests import Request
from starlette.responses import JSONResponse

from app.agents import get_agent_graph
from app.contracts import AgentName, AgentRequest

logger = logging.getLogger(__name__)

_INVOKE_TIMEOUT: float = float(os.environ.get("AGENT_INVOKE_TIMEOUT_SECONDS", "30"))

agent_name = AgentName(os.environ.get("BANKING_AGENT_KIND", AgentName.WORKFLOW_PLANNING))
graph = get_agent_graph(agent_name)
app = InvocationAgentServerHost()


@app.invoke_handler
async def handle_invoke(request: Request):
    try:
        payload = AgentRequest.model_validate(await request.json())
    except (UnicodeDecodeError, json.JSONDecodeError, ValidationError) as exc:
        logger.warning("Invalid request payload with error type %s", type(exc).__name__)
        return JSONResponse(
            {"error": "invalid_request", "detail": "Request payload is invalid."},
            status_code=400,
        )

    try:
        state = await asyncio.wait_for(
            graph.ainvoke({"request": payload, "result": None}),
            timeout=_INVOKE_TIMEOUT,
        )
        response_body = state["result"].model_dump(mode="json")
    except asyncio.TimeoutError:
        logger.error(
            "Agent invocation timed out after %.1fs (trace_id=%s)",
            _INVOKE_TIMEOUT,
            payload.trace_id,
        )
        return JSONResponse(
            {"error": "timeout", "detail": f"Agent did not respond within {_INVOKE_TIMEOUT}s"},
            status_code=504,
        )
    except Exception as exc:  # noqa: BLE001
        logger.error(
            "Agent graph failed with error type %s (trace_id=%s)",
            type(exc).__name__,
            payload.trace_id,
        )
        return JSONResponse(
            {"error": "agent_error", "detail": "Agent invocation failed."},
            status_code=500,
        )

    return JSONResponse(response_body)


if __name__ == "__main__":
    app.run()
