import os
import unittest

from app.agents import get_agent_graph
from app.contracts import AgentName, AgentRequest


class AgentGraphTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
        os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)

    async def invoke(self, agent: AgentName, message: str):
        request = AgentRequest(message=message, trace_id="test-trace")
        state = await get_agent_graph(agent).ainvoke({"request": request, "result": None})
        return state["result"]

    async def test_planner_routes_transaction_explanation(self):
        result = await self.invoke(AgentName.WORKFLOW_PLANNING, "Why is this card transaction pending?")
        self.assertEqual(AgentName.TRANSACTION_EXPLANATION, result.selected_agent)
        self.assertFalse(result.requires_approval)

    async def test_planner_routes_dispute_with_approval(self):
        result = await self.invoke(AgentName.WORKFLOW_PLANNING, "Dispute this charge")
        self.assertEqual(AgentName.DISPUTE_PLANNING, result.selected_agent)
        self.assertTrue(result.requires_approval)

    async def test_suspicious_activity_only_requires_approval_for_action(self):
        informational = await self.invoke(AgentName.SUSPICIOUS_ACTIVITY, "This transaction is not mine")
        action = await self.invoke(AgentName.SUSPICIOUS_ACTIVITY, "Freeze my card; this transaction is not mine")
        self.assertFalse(informational.requires_approval)
        self.assertTrue(action.requires_approval)
