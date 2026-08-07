"""Interoperability checks against the official MCP Python SDK.

The unit tests in ``test_mcp_server.py`` prove our server behaves the way our
own orchestrator expects, which is a weaker guarantee than protocol
conformance. These tests instead validate every response with the Pydantic
models published in the official ``mcp`` SDK, so a payload that this repo
happens to tolerate but that the wider MCP ecosystem would reject fails here.
"""

from __future__ import annotations

import unittest
from typing import Any
from unittest.mock import patch

from fastapi.testclient import TestClient
from mcp.types import (
    CallToolResult,
    InitializeResult,
    JSONRPCResponse,
    ListToolsResult,
)

from app.contracts import AgentName, AgentResult
from app.main import app

MCP_ROUTES: dict[str, tuple[AgentName, str]] = {
    "/workflow-planning/mcp": (AgentName.WORKFLOW_PLANNING, "workflow.plan"),
    "/transaction-explanation/mcp": (AgentName.TRANSACTION_EXPLANATION, "transaction.explain"),
    "/suspicious-activity/mcp": (AgentName.SUSPICIOUS_ACTIVITY, "suspicious.assess"),
    "/dispute-planning/mcp": (AgentName.DISPUTE_PLANNING, "dispute.plan"),
}


def _stub_result(agent: AgentName) -> AgentResult:
    return AgentResult(
        agent=agent,
        trace_id="trace-interop",
        intent="interop-probe",
        summary="stub summary",
        risk_level="low",
        requires_approval=False,
        recommended_action="none",
        next_step="none",
    )


class _StubGraph:
    def __init__(self, agent: AgentName) -> None:
        self._agent = agent

    async def ainvoke(self, _state: dict[str, Any]) -> dict[str, Any]:
        return {"result": _stub_result(self._agent)}


def _rpc(client: TestClient, route: str, method: str, params: Any = None) -> dict[str, Any]:
    payload: dict[str, Any] = {"jsonrpc": "2.0", "id": "interop-1", "method": method}
    if params is not None:
        payload["params"] = params
    response = client.post(route, json=payload)
    assert response.status_code == 200, response.text
    body = response.json()
    # Parsing as a JSONRPCResponse enforces the envelope the SDK requires
    # (jsonrpc version, id echo, presence of result) before we inspect it.
    JSONRPCResponse.model_validate(body)
    return body["result"]


class OfficialSdkConformanceTests(unittest.TestCase):
    """Validate our responses using the official SDK's own type definitions."""

    def test_initialize_result_matches_official_schema(self):
        with TestClient(app) as client:
            for route, (_agent, _tool) in MCP_ROUTES.items():
                with self.subTest(route=route):
                    result = _rpc(
                        client,
                        route,
                        "initialize",
                        {
                            "protocolVersion": "2024-11-05",
                            "capabilities": {},
                            "clientInfo": {"name": "official-mcp-sdk", "version": "1.0"},
                        },
                    )

                    parsed = InitializeResult.model_validate(result)

                    self.assertIsNotNone(parsed.capabilities.tools)
                    self.assertTrue(parsed.serverInfo.name)

    def test_tools_list_result_matches_official_schema(self):
        with TestClient(app) as client:
            for route, (_agent, tool_name) in MCP_ROUTES.items():
                with self.subTest(route=route):
                    result = _rpc(client, route, "tools/list")

                    parsed = ListToolsResult.model_validate(result)

                    self.assertEqual([tool_name], [tool.name for tool in parsed.tools])
                    tool = parsed.tools[0]
                    self.assertEqual("object", tool.inputSchema.get("type"))
                    self.assertTrue(tool.description)

    def test_tools_call_result_matches_official_schema(self):
        for route, (agent, tool_name) in MCP_ROUTES.items():
            with self.subTest(route=route):
                with patch(
                    "app.main.get_agent_graph",
                    return_value=_StubGraph(agent),
                ), TestClient(app) as client:
                    result = _rpc(
                        client,
                        route,
                        "tools/call",
                        {
                            "name": tool_name,
                            "arguments": {
                                "user_message": "interop probe",
                                "trace_id": "trace-interop",
                                "workflow_id": "workflow-interop",
                            },
                        },
                    )

                    parsed = CallToolResult.model_validate(result)

                    self.assertFalse(parsed.isError)
                    self.assertTrue(parsed.content)
                    self.assertEqual("text", parsed.content[0].type)

    def test_unknown_tool_is_reported_as_a_spec_compliant_error(self):
        with TestClient(app) as client:
            response = client.post(
                "/dispute-planning/mcp",
                json={
                    "jsonrpc": "2.0",
                    "id": "interop-err",
                    "method": "tools/call",
                    "params": {"name": "transaction.explain", "arguments": {}},
                },
            )

            body = response.json()
            self.assertEqual("2.0", body["jsonrpc"])
            self.assertEqual("interop-err", body["id"])
            self.assertNotIn("result", body)
            self.assertEqual(-32602, body["error"]["code"])


if __name__ == "__main__":
    unittest.main()
