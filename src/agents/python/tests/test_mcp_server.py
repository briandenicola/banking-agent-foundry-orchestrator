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


# Tool names must match the FOUNDRY_MCP_TOOL_ENDPOINTS keys in apps/main.tf and
# ReadinessChecks.RequiredMcpTools. A rename on either side breaks routing, so
# the mapping is asserted explicitly rather than derived from the server.
EXPECTED_TOOLS = {
    AgentName.WORKFLOW_PLANNING: "workflow.plan",
    AgentName.TRANSACTION_EXPLANATION: "transaction.explain",
    AgentName.SUSPICIOUS_ACTIVITY: "suspicious.assess",
    AgentName.DISPUTE_PLANNING: "dispute.plan",
}


class AllAgentsMcpTests(unittest.IsolatedAsyncioTestCase):
    """Every hosted agent must speak MCP, not just transaction-explanation."""

    async def _post(self, payload):
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=hosted_module.app),
            base_url="http://test",
        ) as client:
            return await client.post("/mcp", json=payload)

    async def test_every_agent_advertises_its_own_tool(self):
        for agent, expected_tool in EXPECTED_TOOLS.items():
            with self.subTest(agent=agent):
                with patch.object(hosted_module, "agent_name", agent):
                    response = await self._post(
                        {"jsonrpc": "2.0", "id": "list", "method": "tools/list", "params": {}}
                    )

                tools = response.json()["result"]["tools"]
                self.assertEqual(1, len(tools))
                self.assertEqual(expected_tool, tools[0]["name"])
                self.assertEqual(
                    ["user_message", "trace_id", "workflow_id"],
                    tools[0]["inputSchema"]["required"],
                )

    async def test_every_agent_invokes_its_own_tool(self):
        for agent, tool in EXPECTED_TOOLS.items():
            with self.subTest(agent=agent):
                graph = _graph(
                    AgentResult(
                        agent=agent,
                        trace_id="trace-1",
                        intent="intent",
                        summary="Handled.",
                        risk_level="low",
                        requires_approval=False,
                        recommended_action="Respond.",
                        next_step="respond_to_user",
                    )
                )
                with (
                    patch.object(hosted_module, "agent_name", agent),
                    patch.object(hosted_module, "graph", graph),
                ):
                    response = await self._post(
                        {
                            "jsonrpc": "2.0",
                            "id": "call",
                            "method": "tools/call",
                            "params": {
                                "name": tool,
                                "arguments": {
                                    "user_message": "Help me.",
                                    "trace_id": "trace-1",
                                    "workflow_id": "wf-1",
                                },
                            },
                        }
                    )

                self.assertEqual(200, response.status_code)
                body = response.json()
                self.assertNotIn("error", body)
                self.assertEqual(agent.value, body["result"]["structuredContent"]["agent"])
                graph.ainvoke.assert_awaited_once()

    async def test_agent_rejects_a_tool_hosted_by_a_different_agent(self):
        for agent, own_tool in EXPECTED_TOOLS.items():
            other_tool = next(t for t in EXPECTED_TOOLS.values() if t != own_tool)
            with self.subTest(agent=agent):
                with patch.object(hosted_module, "agent_name", agent):
                    response = await self._post(
                        {
                            "jsonrpc": "2.0",
                            "id": "call",
                            "method": "tools/call",
                            "params": {"name": other_tool, "arguments": {}},
                        }
                    )

                body = response.json()
                self.assertEqual(-32602, body["error"]["code"])
                self.assertIn(own_tool, body["error"]["message"])

    async def test_initialize_identifies_the_hosting_agent(self):
        for agent in EXPECTED_TOOLS:
            with self.subTest(agent=agent):
                with patch.object(hosted_module, "agent_name", agent):
                    response = await self._post(
                        {"jsonrpc": "2.0", "id": "init", "method": "initialize", "params": {}}
                    )

                server_info = response.json()["result"]["serverInfo"]
                self.assertIn(agent.value, server_info["name"])


if __name__ == "__main__":
    unittest.main()
