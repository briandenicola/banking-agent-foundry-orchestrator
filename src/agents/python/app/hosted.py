from __future__ import annotations

import asyncio
import json
import logging
import os

from azure.ai.agentserver.invocations import InvocationAgentServerHost
from pydantic import ValidationError
from starlette.requests import Request
from starlette.responses import JSONResponse
from starlette.routing import Route

from app.agents import get_agent_graph
from app.contracts import AgentName, AgentRequest
from app.mcp_server import handle_mcp_request

logger = logging.getLogger(__name__)

# Foundry reserves every AGENT_* and FOUNDRY_* environment variable for platform
# use, so this budget must carry the BANKING_ prefix like BANKING_AGENT_KIND.
_INVOKE_TIMEOUT: float = float(
    os.environ.get("BANKING_AGENT_INVOKE_TIMEOUT_SECONDS", "90")
)

agent_name = AgentName(os.environ.get("BANKING_AGENT_KIND", AgentName.WORKFLOW_PLANNING))
graph = get_agent_graph(agent_name)


async def _invoke_graph(payload: AgentRequest):
    return await graph.ainvoke({"request": payload, "result": None})


async def handle_mcp(request: Request):
    return await handle_mcp_request(
        request,
        agent_name=agent_name,
        invoke_graph=_invoke_graph,
        invoke_timeout=_INVOKE_TIMEOUT,
    )


app = InvocationAgentServerHost(routes=[Route("/mcp", handle_mcp, methods=["POST"])])


@app.invoke_handler
async def handle_invoke(request: Request):
    try:
        body = await request.json()
        if isinstance(body, dict) and body.get("jsonrpc") == "2.0":
            async def replay_json():
                return body

            request.json = replay_json  # type: ignore[method-assign]
            return await handle_mcp(request)
        payload = AgentRequest.model_validate(body)
    except (UnicodeDecodeError, json.JSONDecodeError, ValidationError) as exc:
        logger.warning("Invalid request payload with error type %s", type(exc).__name__)
        return JSONResponse(
            {"error": "invalid_request", "detail": "Request payload is invalid."},
            status_code=400,
        )

    try:
        state = await asyncio.wait_for(
            _invoke_graph(payload),
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
