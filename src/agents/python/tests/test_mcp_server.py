from __future__ import annotations

import unittest
from unittest.mock import AsyncMock, MagicMock, patch

import httpx

import app.hosted as hosted_module
from app.contracts import AgentName, AgentResult


def _graph(result: AgentResult | None = None):
    graph = MagicMock()
    graph.ainvoke = AsyncMock(
        return_value={
            "result": result
            or AgentResult(
                agent=AgentName.TRANSACTION_EXPLANATION,
                trace_id="trace-1",
                intent="transaction_explanation",
                summary="The transaction is pending.",
                risk_level="low",
                requires_approval=False,
                recommended_action="Explain the hold.",
                next_step="respond_to_user",
            )
        }
    )
    return graph


class TransactionExplanationMcpTests(unittest.IsolatedAsyncioTestCase):
    async def _post(self, payload):
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=hosted_module.app),
            base_url="http://test",
        ) as client:
            return await client.post("/mcp", json=payload)

    async def test_initialize_negotiates_tools_capability(self):
        with patch.object(hosted_module, "agent_name", AgentName.TRANSACTION_EXPLANATION):
            response = await self._post(
                {"jsonrpc": "2.0", "id": "init-1", "method": "initialize", "params": {}}
            )

        self.assertEqual(200, response.status_code)
        body = response.json()
        self.assertEqual("2.0", body["jsonrpc"])
        self.assertEqual("init-1", body["id"])
        self.assertIn("tools", body["result"]["capabilities"])

    async def test_tools_list_exposes_transaction_explanation_schema(self):
        with patch.object(hosted_module, "agent_name", AgentName.TRANSACTION_EXPLANATION):
            response = await self._post(
                {"jsonrpc": "2.0", "id": "list-1", "method": "tools/list", "params": {}}
            )

        tool = response.json()["result"]["tools"][0]
        self.assertEqual("transaction.explain", tool["name"])
        self.assertEqual(["user_message", "trace_id", "workflow_id"], tool["inputSchema"]["required"])

    async def test_tools_call_invokes_transaction_graph(self):
        graph = _graph()
        with (
            patch.object(hosted_module, "agent_name", AgentName.TRANSACTION_EXPLANATION),
            patch.object(hosted_module, "graph", graph),
        ):
            response = await self._post(
                {
                    "jsonrpc": "2.0",
                    "id": "call-1",
                    "method": "tools/call",
                    "params": {
                        "name": "transaction.explain",
                        "arguments": {
                            "user_message": "Why is this pending?",
                            "trace_id": "trace-1",
                            "workflow_id": "wf-1",
                        },
                    },
                }
            )

        self.assertEqual(200, response.status_code)
        body = response.json()
        self.assertFalse(body["result"]["isError"])
        self.assertEqual("transaction-explanation", body["result"]["structuredContent"]["agent"])
        graph.ainvoke.assert_awaited_once()

    async def test_unknown_tool_returns_jsonrpc_invalid_params_error(self):
        with patch.object(hosted_module, "agent_name", AgentName.TRANSACTION_EXPLANATION):
            response = await self._post(
                {
                    "jsonrpc": "2.0",
                    "id": "call-2",
                    "method": "tools/call",
                    "params": {"name": "workflow.plan", "arguments": {}},
                }
            )

        body = response.json()
        self.assertEqual(-32602, body["error"]["code"])
        self.assertEqual("call-2", body["id"])

    async def test_parse_error_uses_jsonrpc_error_object(self):
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=hosted_module.app),
            base_url="http://test",
        ) as client:
            response = await client.post("/mcp", content=b'{"jsonrpc":')

        self.assertEqual(-32700, response.json()["error"]["code"])


if __name__ == "__main__":
    unittest.main()
