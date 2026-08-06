import json
import os
import pathlib
import unittest
from unittest.mock import AsyncMock, Mock, patch

from pydantic import ValidationError

from app.agents import get_agent_graph
from app.contracts import CONTRACT_VERSION, AgentName, AgentRequest, AgentResult
from app.model import reason

# Repository root — used to load shared fixtures.
_REPO_ROOT = pathlib.Path(__file__).resolve().parents[4]
_FIXTURE = _REPO_ROOT / "tests" / "fixtures" / "hosted-agent-invocation-v1.json"


class AgentGraphTests(unittest.IsolatedAsyncioTestCase):
    def setUp(self):
        os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
        os.environ.pop("BANKING_AGENT_PROJECT_ENDPOINT", None)
        os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)
        os.environ.pop("ALLOW_FALLBACK", None)

    async def invoke(self, agent: AgentName, message: str):
        request = AgentRequest(message=message, trace_id="test-trace")
        state = await get_agent_graph(agent).ainvoke({"request": request, "result": None})
        return state["result"]

    async def test_planner_routes_transaction_explanation(self):
        os.environ["ALLOW_FALLBACK"] = "true"
        result = await self.invoke(AgentName.WORKFLOW_PLANNING, "Why is this card transaction pending?")
        self.assertEqual(AgentName.TRANSACTION_EXPLANATION, result.selected_agent)
        self.assertFalse(result.requires_approval)

    async def test_planner_routes_dispute_with_approval(self):
        os.environ["ALLOW_FALLBACK"] = "true"
        result = await self.invoke(AgentName.WORKFLOW_PLANNING, "Dispute this charge")
        self.assertEqual(AgentName.DISPUTE_PLANNING, result.selected_agent)
        self.assertTrue(result.requires_approval)

    # ------------------------------------------------------------------
    # Planner routing criteria (regression guard for #39)
    #
    # The deployed mis-routing happened on the *model* path, not the
    # deterministic fallback: the fallback below already routes this
    # message correctly, which is why the fallback tests stayed green
    # while production sent informational messages to dispute-planning.
    # The instructions the planner sends to the model are therefore the
    # artifact under test.
    # ------------------------------------------------------------------

    async def _planner_system_prompt(self) -> str:
        """Invoke the real planner graph against a mocked model and return
        the system prompt it sent, whitespace-normalised so assertions do
        not depend on how the instructions happen to be line-wrapped."""
        structured_model = Mock()
        structured_model.ainvoke = AsyncMock(
            return_value=AgentResult(
                agent=AgentName.WORKFLOW_PLANNING,
                trace_id="test-trace",
                intent="suspicious_activity",
                summary="Classified the request.",
                risk_level="high",
                requires_approval=False,
                recommended_action="Invoke the selected specialist agent.",
                next_step="invoke_specialist",
                selected_agent=AgentName.SUSPICIOUS_ACTIVITY,
            )
        )
        model_instance = Mock()
        model_instance.with_structured_output.return_value = structured_model

        with patch("app.model._model", return_value=model_instance):
            await self.invoke(
                AgentName.WORKFLOW_PLANNING,
                "This transaction is not mine. Explain what I should review.",
            )

        messages = structured_model.ainvoke.await_args.args[0]
        system_prompt = next(content for role, content in messages if role == "system")
        return " ".join(system_prompt.split())

    async def test_planner_prompt_restricts_dispute_to_explicit_requests(self):
        """A customer saying a charge is not theirs must not be read as a
        dispute request. Reverting to instructions that merely list the
        three agents fails this."""
        prompt = await self._planner_system_prompt()
        self.assertIn("ONLY when the customer explicitly asks to dispute", prompt)
        self.assertIn(
            "unrecognized activity alone is suspicious-activity",
            prompt,
        )

    async def test_planner_prompt_separates_risk_from_approval(self):
        """High risk must not by itself force approval; only a requested
        account-changing action may."""
        prompt = await self._planner_system_prompt()
        self.assertIn(
            "Requests for explanation, guidance, or next steps never require approval",
            prompt,
        )

    async def test_planner_routes_informational_unrecognized_charge_without_approval(self):
        """The exact phrasing asserted by scripts/smoke-mvp.py."""
        os.environ["ALLOW_FALLBACK"] = "true"
        result = await self.invoke(
            AgentName.WORKFLOW_PLANNING,
            "This transaction is not mine. Explain what I should review.",
        )
        self.assertEqual(AgentName.SUSPICIOUS_ACTIVITY, result.selected_agent)
        self.assertFalse(result.requires_approval)

    async def test_planner_escalates_unrecognized_charge_when_action_requested(self):
        os.environ["ALLOW_FALLBACK"] = "true"
        result = await self.invoke(
            AgentName.WORKFLOW_PLANNING,
            "Freeze my card; this transaction is not mine.",
        )
        self.assertEqual(AgentName.SUSPICIOUS_ACTIVITY, result.selected_agent)
        self.assertTrue(result.requires_approval)

    async def test_suspicious_activity_only_requires_approval_for_action(self):
        os.environ["ALLOW_FALLBACK"] = "true"
        informational = await self.invoke(AgentName.SUSPICIOUS_ACTIVITY, "This transaction is not mine")
        action = await self.invoke(AgentName.SUSPICIOUS_ACTIVITY, "Freeze my card; this transaction is not mine")
        self.assertFalse(informational.requires_approval)
        self.assertTrue(action.requires_approval)

    async def test_successful_model_invocation_owns_operational_status(self):
        structured_model = Mock()
        structured_model.ainvoke = AsyncMock(
            return_value=AgentResult(
                agent=AgentName.TRANSACTION_EXPLANATION,
                status="error",
                trace_id="model-selected-trace",
                intent="transaction_explanation",
                summary="The model completed its analysis.",
                risk_level="low",
                requires_approval=False,
                recommended_action="Explain the transaction.",
                next_step="respond_to_user",
            )
        )
        model = Mock()
        model.with_structured_output.return_value = structured_model
        request = AgentRequest(message="Explain this transaction.", trace_id="request-trace")

        with patch("app.model._model", return_value=model):
            result = await reason(
                AgentName.TRANSACTION_EXPLANATION,
                "Explain the transaction.",
                request,
            )

        self.assertEqual("ok", result.status)
        self.assertEqual("request-trace", result.trace_id)

    # ------------------------------------------------------------------
    # Contract version 1.0 fixture
    # ------------------------------------------------------------------

    def test_fixture_file_exists(self):
        self.assertTrue(_FIXTURE.exists(), f"Fixture not found: {_FIXTURE}")

    def test_v1_fixture_accepted_by_agent_request(self):
        raw = json.loads(_FIXTURE.read_text())
        req = AgentRequest.model_validate(raw)

        self.assertEqual(CONTRACT_VERSION, req.contract_version)
        self.assertEqual("Dispute demo transaction DEMO-TXN-1001.", req.message)
        self.assertEqual("0123456789abcdef0123456789abcdef", req.trace_id)
        self.assertEqual("11111111-1111-1111-1111-111111111111", req.workflow_id)

    def test_unsupported_contract_version_is_rejected(self):
        with self.assertRaises(ValidationError):
            AgentRequest(message="hello", contract_version="9.9")

    def test_v1_fixture_yields_specialist_context(self):
        raw = json.loads(_FIXTURE.read_text())
        req = AgentRequest.model_validate(raw)

        ctx = req.specialist_context
        self.assertEqual("dispute-planning", ctx.get("selected_agent"))
        self.assertEqual("dispute-planning", ctx.get("planner_selected_agent"))

    async def test_v1_fixture_context_reaches_model_prompt(self):
        """The normalized specialist context derived from the shared v1
        fixture must be threaded through to the prompt sent to the model,
        so a mocked structured model can be asserted to have received the
        planner summary and selected agent from the fixture's context."""
        raw = json.loads(_FIXTURE.read_text())
        request = AgentRequest.model_validate(raw)
        ctx = request.specialist_context

        structured_model = Mock()
        structured_model.ainvoke = AsyncMock(
            return_value=AgentResult(
                agent=AgentName.DISPUTE_PLANNING,
                trace_id=request.trace_id,
                intent="dispute",
                summary="Prepared the dispute.",
                risk_level="high",
                requires_approval=True,
                recommended_action="Collect dispute details.",
                next_step="request_approval",
            )
        )
        model_instance = Mock()
        model_instance.with_structured_output.return_value = structured_model

        with patch("app.model._model", return_value=model_instance):
            await reason(AgentName.DISPUTE_PLANNING, "instructions", request)

        messages = structured_model.ainvoke.await_args.args[0]
        user_prompt = next(content for role, content in messages if role == "user")
        self.assertIn(ctx["planner_summary"], user_prompt)
        self.assertIn(ctx["selected_agent"], user_prompt)

    # ------------------------------------------------------------------
    # Context normalisation
    # ------------------------------------------------------------------

    def test_top_level_context_is_authoritative(self):
        """When both top-level context and input.context exist, top-level wins."""
        req = AgentRequest(
            message="hello",
            context={"source": "top-level"},
            input={"context": {"source": "legacy"}},
        )
        self.assertEqual("top-level", req.specialist_context["source"])

    def test_explicit_empty_top_level_context_is_authoritative(self):
        req = AgentRequest(
            message="hello",
            context={},
            input={"context": {"source": "legacy"}},
        )
        self.assertEqual({}, req.specialist_context)

    def test_legacy_input_context_promoted_when_top_level_absent(self):
        """When top-level context is omitted, input.context is promoted."""
        req = AgentRequest(
            message="hello",
            input={"context": {"source": "legacy", "planner_selected_agent": "dispute-planning"}},
        )
        self.assertEqual("legacy", req.specialist_context["source"])
        self.assertEqual("dispute-planning", req.specialist_context["planner_selected_agent"])

    def test_empty_context_when_neither_present(self):
        req = AgentRequest(message="hello")
        self.assertEqual({}, req.specialist_context)

    # ------------------------------------------------------------------
    # Runtime-owned result fields
    # ------------------------------------------------------------------

    async def test_model_cannot_override_runtime_owned_fields(self):
        """Model output for agent, status, trace_id, contract_version, and
        execution_mode must be replaced by runtime-injected values."""
        structured_model = Mock()
        structured_model.ainvoke = AsyncMock(
            return_value=AgentResult(
                agent=AgentName.DISPUTE_PLANNING,      # model says wrong agent
                status="error",                         # model says error
                trace_id="model-injected-trace",        # model says wrong trace
                execution_mode="fallback",              # model says fallback
                contract_version="9.9",                 # model says wrong version
                intent="dispute",
                summary="ok",
                risk_level="high",
                requires_approval=True,
                recommended_action="none",
                next_step="request_approval",
            )
        )
        model_instance = Mock()
        model_instance.with_structured_output.return_value = structured_model
        request = AgentRequest(message="Dispute this.", trace_id="runtime-trace")

        with patch("app.model._model", return_value=model_instance):
            result = await reason(AgentName.TRANSACTION_EXPLANATION, "instructions", request)

        self.assertEqual(AgentName.TRANSACTION_EXPLANATION, result.agent)
        self.assertEqual("ok", result.status)
        self.assertEqual("runtime-trace", result.trace_id)
        self.assertEqual("model", result.execution_mode)
        self.assertEqual(CONTRACT_VERSION, result.contract_version)

    # ------------------------------------------------------------------
    # Fallback policy
    # ------------------------------------------------------------------

    async def test_fallback_path_sets_execution_mode_fallback(self):
        os.environ.pop("BANKING_AGENT_PROJECT_ENDPOINT", None)
        os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)
        os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
        os.environ["ALLOW_FALLBACK"] = "true"
        request = AgentRequest(message="Dispute this charge", trace_id="t1")
        result = await reason(AgentName.WORKFLOW_PLANNING, "instructions", request)
        self.assertEqual("fallback", result.execution_mode)

    async def test_fallback_disabled_raises_when_no_model(self):
        os.environ["ALLOW_FALLBACK"] = "false"
        os.environ.pop("BANKING_AGENT_PROJECT_ENDPOINT", None)
        os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)
        os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
        request = AgentRequest(message="Dispute this charge", trace_id="t1")
        with self.assertRaises(RuntimeError):
            await reason(AgentName.WORKFLOW_PLANNING, "instructions", request)

    async def test_model_error_is_not_masked_as_fallback_success(self):
        """A live model call that raises must propagate — never be silently
        downgraded into a success-shaped deterministic fallback response."""
        os.environ["BANKING_AGENT_PROJECT_ENDPOINT"] = "https://example.test"
        os.environ["ALLOW_FALLBACK"] = "true"

        broken_model = Mock()
        broken_model.with_structured_output.return_value = Mock(
            ainvoke=AsyncMock(side_effect=RuntimeError("model error"))
        )
        request = AgentRequest(message="Dispute this charge", trace_id="t1")
        with patch("app.model._model", return_value=broken_model):
            with self.assertRaises(RuntimeError):
                await reason(AgentName.WORKFLOW_PLANNING, "instructions", request)

    async def test_fallback_disabled_when_allow_fallback_unset(self):
        """Unset ALLOW_FALLBACK must disable fallback (strict opt-in)."""
        os.environ.pop("ALLOW_FALLBACK", None)
        os.environ.pop("BANKING_AGENT_PROJECT_ENDPOINT", None)
        os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)
        os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
        request = AgentRequest(message="Dispute this charge", trace_id="t1")
        with self.assertRaises(RuntimeError):
            await reason(AgentName.WORKFLOW_PLANNING, "instructions", request)

    async def test_fallback_enabled_for_truthy_values(self):
        """Affirmative ALLOW_FALLBACK variants all enable fallback."""
        request = AgentRequest(message="Dispute this charge", trace_id="t1")
        for value in ("true", "True", "TRUE", "1", "yes", "YES", "on", "ON", " true "):
            with self.subTest(value=value):
                os.environ.pop("BANKING_AGENT_PROJECT_ENDPOINT", None)
                os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)
                os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
                os.environ["ALLOW_FALLBACK"] = value
                result = await reason(AgentName.WORKFLOW_PLANNING, "instructions", request)
                self.assertEqual("fallback", result.execution_mode)

    async def test_fallback_disabled_for_falsy_values(self):
        """Non-affirmative ALLOW_FALLBACK variants all disable fallback."""
        request = AgentRequest(message="Dispute this charge", trace_id="t1")
        for value in ("false", "False", "FALSE", "0", "no", "NO", "off", "OFF", "", "maybe", "  "):
            with self.subTest(value=value):
                os.environ.pop("BANKING_AGENT_PROJECT_ENDPOINT", None)
                os.environ.pop("FOUNDRY_PROJECT_ENDPOINT", None)
                os.environ.pop("AZURE_OPENAI_ENDPOINT", None)
                os.environ["ALLOW_FALLBACK"] = value
                with self.assertRaises(RuntimeError):
                    await reason(AgentName.WORKFLOW_PLANNING, "instructions", request)


if __name__ == "__main__":
    unittest.main()
