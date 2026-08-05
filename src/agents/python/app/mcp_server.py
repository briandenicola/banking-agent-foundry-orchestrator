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
TRANSACTION_TOOL_NAME = "transaction.explain"
TRANSACTION_AGENT = AgentName.TRANSACTION_EXPLANATION

PARSE_ERROR = -32700
INVALID_REQUEST = -32600
METHOD_NOT_FOUND = -32601
INVALID_PARAMS = -32602
INTERNAL_ERROR = -32603

GraphInvoker = Callable[[AgentRequest], Awaitable[dict[str, Any]]]


def transaction_explanation_tool() -> dict[str, Any]:
    return {
        "name": TRANSACTION_TOOL_NAME,
        "description": "Explain a banking transaction without taking sensitive action.",
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
            "properties": {
                "agent": {"type": "string"},
                "status": {"type": "string"},
                "summary": {"type": "string"},
                "requires_approval": {"type": "boolean"},
            },
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
                "serverInfo": {"name": "banking-transaction-explanation-agent", "version": "1.0"},
            },
            request_id,
        )

    if method == "tools/list":
        tools = [transaction_explanation_tool()] if agent_name == TRANSACTION_AGENT else []
        return _result({"tools": tools}, request_id)

    if method != "tools/call":
        return _error(METHOD_NOT_FOUND, f"Unknown method: {method}", request_id)

    tool_name = params.get("name")
    arguments = params.get("arguments", {})
    if tool_name != TRANSACTION_TOOL_NAME:
        return _error(INVALID_PARAMS, f"Unknown tool: {tool_name}", request_id)
    if agent_name != TRANSACTION_AGENT:
        return _error(INVALID_PARAMS, f"Tool {TRANSACTION_TOOL_NAME} is not hosted by {agent_name}.", request_id)
    if not isinstance(arguments, dict):
        return _error(INVALID_PARAMS, "arguments must be an object.", request_id)

    try:
        agent_request = AgentRequest(
            message=str(arguments.get("user_message") or arguments.get("message") or ""),
            trace_id=str(arguments.get("trace_id") or "unknown"),
            workflow_id=arguments.get("workflow_id"),
            tool_name=TRANSACTION_TOOL_NAME,
            agent_name=TRANSACTION_AGENT.value,
            input=arguments,
            metadata={"transport": "mcp-jsonrpc-2.0"},
            context=arguments.get("context") if isinstance(arguments.get("context"), dict) else {},
        )
    except ValidationError:
        return _error(INVALID_PARAMS, "arguments do not satisfy the transaction explanation schema.", request_id)

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
