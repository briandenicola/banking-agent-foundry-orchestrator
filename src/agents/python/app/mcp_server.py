from __future__ import annotations

import asyncio
import json
import logging
from collections.abc import Awaitable, Callable
from typing import Any

from pydantic import ValidationError
from starlette.requests import Request
from starlette.responses import JSONResponse, Response

from app.contracts import AgentName, AgentRequest

logger = logging.getLogger(__name__)

JSONRPC_VERSION = "2.0"
MCP_PROTOCOL_VERSION = "2024-11-05"

# Every hosted agent exposes exactly one tool over MCP. The tool name must match
# the key the orchestrator uses in FOUNDRY_MCP_TOOL_ENDPOINTS (see apps/main.tf)
# and in ReadinessChecks.RequiredMcpTools.
_AGENT_TOOLS: dict[AgentName, tuple[str, str]] = {
    AgentName.WORKFLOW_PLANNING: (
        "workflow.plan",
        "Classify a customer's banking request, select exactly one specialist "
        "agent, assess risk, and decide whether human approval is required. "
        "Never executes an action.",
    ),
    AgentName.TRANSACTION_EXPLANATION: (
        "transaction.explain",
        "Explain a banking transaction without taking sensitive action.",
    ),
    AgentName.SUSPICIOUS_ACTIVITY: (
        "suspicious.assess",
        "Assess suspicious or unrecognized account activity, separate observed "
        "facts from hypotheses, and recommend protective next steps. Any request "
        "to freeze, block, or close an account requires approval.",
    ),
    AgentName.DISPUTE_PLANNING: (
        "dispute.plan",
        "Prepare a bounded transaction-dispute plan covering missing information, "
        "eligibility, and evidence requirements. Never submits a dispute.",
    ),
}

PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603

GraphInvoker = Callable[[AgentRequest], Awaitable[dict[str, Any]]]


def tool_name_for(agent_name: AgentName) -> str:
    """The MCP tool name this agent hosts."""
    return _AGENT_TOOLS[agent_name][0]


def tool_definition(agent_name: AgentName) -> dict[str, Any]:
    """The MCP tool descriptor advertised by this agent via tools/list."""
    name, description = _AGENT_TOOLS[agent_name]

    output_properties: dict[str, Any] = {
        "agent": {"type": "string"},
        "status": {"type": "string"},
        "summary": {"type": "string"},
        "requires_approval": {"type": "boolean"},
    }
    if agent_name == AgentName.WORKFLOW_PLANNING:
        output_properties["selected_agent"] = {"type": "string"}

    return {
        "name": name,
        "description": description,
        "inputSchema": {
            "type": "object",
            "properties": {
                "user_message": {"type": "string"},
                "trace_id": {"type": "string"},
                "workflow_id": {"type": "string"},
                "context": {"type": "object"},
                "correlation_id": {"type": "string"},
                "workflow_status": {"type": "string"},
                "intent": {"type": "string"},
            },
            "required": ["user_message", "trace_id", "workflow_id"],
            "additionalProperties": True,
        },
        "outputSchema": {
            "type": "object",
            "properties": output_properties,
            "additionalProperties": True,
        },
    }



def _error(code: int, message: str, request_id: Any = None) -> JSONResponse:
    return JSONResponse(
        {"jsonrpc": JSONRPC_VERSION, "id": request_id, "error": {"code": code, "message": message}}
    )


def _result(result: dict[str, Any], request_id: Any) -> JSONResponse:
    return JSONResponse({"jsonrpc": JSONRPC_VERSION, "id": request_id, "result": result})


def _validate_jsonrpc_request(payload: Any) -> tuple[str, Any, dict[str, Any] | None, JSONResponse | None]:
    if not isinstance(payload, dict):
        return "", None, None, _error(INVALID_REQUEST, "JSON-RPC request must be an object.")

    request_id = payload.get("id")
    if payload.get("jsonrpc") != JSONRPC_VERSION:
        return "", request_id, None, _error(INVALID_REQUEST, "jsonrpc must be '2.0'.", request_id)

    method = payload.get("method")
    if not isinstance(method, str):
        return "", request_id, None, _error(INVALID_REQUEST, "method must be a string.", request_id)

    params = payload.get("params", {})
    if params is None:
        params = {}
    if not isinstance(params, dict):
        return method, request_id, None, _error(INVALID_PARAMS, "params must be an object.", request_id)

    return method, request_id, params, None


async def handle_mcp_request(
    request: Request,
    *,
    agent_name: AgentName,
    invoke_graph: GraphInvoker,
    invoke_timeout: float,
) -> Response:
    try:
        payload = await request.json()
    except (UnicodeDecodeError, json.JSONDecodeError):
        return _error(PARSE_ERROR, "Parse error.")

    method, request_id, params, validation_error = _validate_jsonrpc_request(payload)
    if validation_error is not None:
        return validation_error
    if request_id is None:
        return Response(status_code=204)

    if method == "initialize":
        return _result(
            {
                "protocolVersion": MCP_PROTOCOL_VERSION,
                "capabilities": {"tools": {"listChanged": False}},
                "serverInfo": {"name": f"banking-{agent_name.value}-agent", "version": "1.0"},
            },
            request_id,
        )

    if method == "tools/list":
        return _result({"tools": [tool_definition(agent_name)]}, request_id)

    if method != "tools/call":
        return _error(METHOD_NOT_FOUND, f"Unknown method: {method}", request_id)

    hosted_tool_name = tool_name_for(agent_name)
    tool_name = params.get("name")
    arguments = params.get("arguments", {})
    if tool_name != hosted_tool_name:
        return _error(
            INVALID_PARAMS,
            f"Unknown tool: {tool_name}. This agent hosts {hosted_tool_name}.",
            request_id,
        )
    if not isinstance(arguments, dict):
        return _error(INVALID_PARAMS, "arguments must be an object.", request_id)

    try:
        agent_request = AgentRequest(
            message=str(arguments.get("user_message") or arguments.get("message") or ""),
            trace_id=str(arguments.get("trace_id") or "unknown"),
            workflow_id=arguments.get("workflow_id"),
            tool_name=hosted_tool_name,
            agent_name=agent_name.value,
            input=arguments,
            metadata={"transport": "mcp-jsonrpc-2.0"},
            context=arguments.get("context") if isinstance(arguments.get("context"), dict) else {},
        )
    except ValidationError:
        return _error(
            INVALID_PARAMS,
            f"arguments do not satisfy the {hosted_tool_name} schema.",
            request_id,
        )

    try:
        state = await asyncio.wait_for(invoke_graph(agent_request), timeout=invoke_timeout)
        result = state["result"].model_dump(mode="json")
    except asyncio.TimeoutError:
        return _error(INTERNAL_ERROR, f"Agent did not respond within {invoke_timeout}s", request_id)
    except Exception as exc:  # noqa: BLE001
        logger.error("MCP tool call failed with error type %s", type(exc).__name__)
        return _error(INTERNAL_ERROR, "Agent invocation failed.", request_id)

    return _result(
        {
            "content": [{"type": "text", "text": json.dumps(result, separators=(",", ":"))}],
            "structuredContent": result,
            "isError": result.get("status") == "error",
        },
        request_id,
    )
