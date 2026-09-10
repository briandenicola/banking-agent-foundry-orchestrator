"""The remembered contact preference has to survive the trip into the prompt.

The orchestrator decides what the deployment can do about a channel a customer
asked for and puts the answer in the specialist context. That context is
interpolated into the prompt as a stringified dictionary, where an instruction
reads as just another key -- which is how an agent ends up agreeing to text a
customer that nothing can text.

These tests pin the two places the answer has to land: the prompt sent to the
model, and the canned answer used when there is no model at all.
"""

import unittest
from unittest.mock import AsyncMock, Mock, patch

from app.contracts import AgentName, AgentRequest
from app.model import apply_contact_channel_note, contact_channel_directive, reason


class ContactChannelDirectiveTests(unittest.TestCase):
    def test_guidance_is_raised_out_of_the_context_dictionary(self):
        directive = contact_channel_directive(
            {
                "contact_channel": "sms",
                "contact_channel_status": "known_unavailable",
                "contact_channel_guidance": "Do not promise SMS.",
            }
        )

        self.assertIn("Do not promise SMS.", directive)
        self.assertIn("follow this exactly", directive)

    def test_a_context_without_guidance_adds_nothing(self):
        self.assertEqual(contact_channel_directive({"reference": "TX-1"}), "")
        self.assertEqual(contact_channel_directive({}), "")

    def test_blank_or_malformed_guidance_adds_nothing(self):
        # An empty directive header would tell the model to follow nothing
        # exactly, which is worse than staying quiet.
        self.assertEqual(contact_channel_directive({"contact_channel_guidance": "   "}), "")
        self.assertEqual(contact_channel_directive({"contact_channel_guidance": 42}), "")


class ContactChannelPromptTests(unittest.IsolatedAsyncioTestCase):
    async def test_the_model_is_given_the_guidance_as_an_instruction(self):
        captured = {}

        async def capture(messages):
            captured["messages"] = messages
            return _ok_result()

        structured = Mock()
        structured.ainvoke = AsyncMock(side_effect=capture)
        model = Mock()
        model.with_structured_output = Mock(return_value=structured)

        request = AgentRequest(
            message="Why is this charge pending?",
            trace_id="t-1",
            context={
                "contact_channel": "sms",
                "contact_channel_status": "known_unavailable",
                "contact_channel_guidance": "Do not promise SMS.",
            },
        )

        with patch("app.model._model", return_value=model):
            await reason(AgentName.TRANSACTION_EXPLANATION, "instructions", request)

        user_message = captured["messages"][1][1]
        self.assertIn("Contact channel requirement", user_message)
        self.assertIn("Do not promise SMS.", user_message)


class ContactChannelGraphFallbackTests(unittest.IsolatedAsyncioTestCase):
    """The path that actually runs in production.

    Specialists execute as graphs whose terminal nodes assemble their own
    ``AgentResult``; they never call :func:`reason`. A fix applied only to
    ``reason`` would be dead code, so this exercises the graph itself with no
    model configured.
    """

    async def test_a_specialist_graph_with_no_model_still_refuses_the_channel(self):
        from app.agents import get_agent_graph

        request = AgentRequest(
            message="Why is this charge pending?",
            trace_id="t-1",
            context={
                "contact_channel": "sms",
                "contact_channel_status": "known_unavailable",
                "contact_channel_guidance": "Do not promise SMS.",
            },
        )

        with patch("app.model._model", return_value=None), patch(
            "app.model._fallback_allowed", return_value=True
        ):
            graph = get_agent_graph(AgentName.TRANSACTION_EXPLANATION)
            state = await graph.ainvoke({"request": request, "result": None})

        result = state["result"]
        self.assertEqual(result.execution_mode, "fallback")
        self.assertIn("cannot send", result.summary)

    async def test_a_dispute_graph_with_no_model_still_refuses_the_channel(self):
        from app.agents import get_agent_graph

        request = AgentRequest(
            message="I want to dispute a charge of 40 dollars at Acme on 3 May.",
            trace_id="t-2",
            context={
                "contact_channel": "sms",
                "contact_channel_status": "known_unavailable",
                "contact_channel_guidance": "Do not promise SMS.",
            },
        )

        with patch("app.model._model", return_value=None), patch(
            "app.model._fallback_allowed", return_value=True
        ):
            graph = get_agent_graph(AgentName.DISPUTE_PLANNING)
            state = await graph.ainvoke({"request": request, "result": None})

        result = state["result"]
        self.assertIn("cannot send", result.summary)
        # The refusal is wording only. A dispute still demands a human.
        self.assertTrue(result.requires_approval)

    async def test_a_model_backed_answer_is_not_given_a_second_canned_refusal(self):
        from app.contracts import CONTRACT_VERSION, AgentResult

        request = AgentRequest(
            message="Why is this charge pending?",
            trace_id="t-1",
            context={
                "contact_channel": "sms",
                "contact_channel_status": "known_unavailable",
                "contact_channel_guidance": "Do not promise SMS.",
            },
        )
        model_answer = AgentResult(
            agent=AgentName.TRANSACTION_EXPLANATION,
            trace_id="t-1",
            contract_version=CONTRACT_VERSION,
            execution_mode="model",
            intent="transaction_explanation",
            summary="I cannot text you, so updates will appear here.",
            risk_level="low",
            requires_approval=False,
            recommended_action="Explain it.",
            next_step="respond_to_user",
            evidence=[],
        )

        unchanged = apply_contact_channel_note(model_answer, request)

        self.assertEqual(unchanged.summary, model_answer.summary)


class ContactChannelFallbackTests(unittest.IsolatedAsyncioTestCase):
    """The deterministic path runs during an outage, which is exactly when a
    silently dropped preference matters most."""

    async def test_the_canned_answer_still_says_the_channel_cannot_be_used(self):
        request = AgentRequest(
            message="Why is this charge pending?",
            trace_id="t-1",
            context={
                "contact_channel": "sms",
                "contact_channel_status": "known_unavailable",
                "contact_channel_guidance": "Do not promise SMS.",
            },
        )

        with patch("app.model._model", return_value=None), patch(
            "app.model._fallback_allowed", return_value=True
        ):
            result = await reason(AgentName.TRANSACTION_EXPLANATION, "instructions", request)

        self.assertIn("sms", result.summary)
        self.assertIn("cannot send", result.summary)
        self.assertEqual(result.execution_mode, "fallback")

    async def test_a_serviceable_channel_adds_no_apology(self):
        request = AgentRequest(
            message="Why is this charge pending?",
            trace_id="t-1",
            context={
                "contact_channel": "secure_message",
                "contact_channel_status": "supported",
                "contact_channel_guidance": "Confirm briefly.",
            },
        )

        with patch("app.model._model", return_value=None), patch(
            "app.model._fallback_allowed", return_value=True
        ):
            result = await reason(AgentName.TRANSACTION_EXPLANATION, "instructions", request)

        self.assertNotIn("cannot send", result.summary)

    async def test_a_customer_with_no_contact_preference_gets_the_unchanged_answer(self):
        request = AgentRequest(message="Why is this charge pending?", trace_id="t-1")

        with patch("app.model._model", return_value=None), patch(
            "app.model._fallback_allowed", return_value=True
        ):
            result = await reason(AgentName.TRANSACTION_EXPLANATION, "instructions", request)

        self.assertNotIn("cannot", result.summary.lower())


def _ok_result():
    from app.contracts import CONTRACT_VERSION, AgentResult

    return AgentResult(
        agent=AgentName.TRANSACTION_EXPLANATION,
        trace_id="t-1",
        contract_version=CONTRACT_VERSION,
        execution_mode="model",
        intent="transaction_explanation",
        summary="Explaining the charge.",
        risk_level="low",
        requires_approval=False,
        recommended_action="Explain it.",
        next_step="respond_to_user",
        evidence=[],
    )


if __name__ == "__main__":
    unittest.main()
