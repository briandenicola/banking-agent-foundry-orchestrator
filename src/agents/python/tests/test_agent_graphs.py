"""Node-level and branch-level tests for the multi-node agent graphs.

These test node functions directly, with stubbed model output, so a node's
behaviour is verified independently of the full graph. Graph-level tests then
assert only the thing a node test cannot: which branch the conditional edge
selects.
"""

from __future__ import annotations

import unittest
from unittest.mock import AsyncMock, patch

from app.agents import dispute, suspicious_activity
from app.contracts import AgentName, AgentRequest


def _request(message: str) -> AgentRequest:
    return AgentRequest(message=message, trace_id="trace-graph", workflow_id="workflow-graph")


class DisputeNodeTests(unittest.IsolatedAsyncioTestCase):
    async def test_extract_claim_records_only_stated_facts(self):
        claim = dispute.DisputeClaim(merchant="ACME", amount="42.00")
        with patch.object(dispute, "structured_step", AsyncMock(return_value=claim)):
            update = await dispute.extract_claim({"request": _request("dispute ACME 42.00")})

        self.assertEqual("ACME", update["claim"].merchant)
        self.assertNotIn("used_fallback", update)

    async def test_extract_claim_without_model_yields_empty_claim(self):
        with patch.object(dispute, "structured_step", AsyncMock(return_value=None)):
            update = await dispute.extract_claim({"request": _request("Dispute this charge.")})

        self.assertEqual(
            ["merchant", "amount", "transaction_date", "reason"],
            update["claim"].missing_fields(),
        )
        self.assertTrue(update["used_fallback"])

    async def test_validate_completeness_overrides_a_model_that_ignores_missing_fields(self):
        # The model is allowed to explain, but not to declare an empty claim
        # complete: doing so would skip the information-request branch.
        state = {"request": _request("Dispute this charge."), "claim": dispute.DisputeClaim()}
        optimistic = dispute.CompletenessCheck(
            is_complete=True, missing_fields=[], rationale="Looks fine to me."
        )
        with patch.object(dispute, "structured_step", AsyncMock(return_value=optimistic)):
            update = await dispute.validate_completeness(state)

        self.assertFalse(update["completeness"].is_complete)
        self.assertEqual(
            ["merchant", "amount", "transaction_date", "reason"],
            update["completeness"].missing_fields,
        )

    async def test_validate_completeness_accepts_a_fully_specified_claim(self):
        claim = dispute.DisputeClaim(
            merchant="ACME", amount="42.00", transaction_date="2026-01-02", reason="unauthorized"
        )
        state = {"request": _request("dispute"), "claim": claim}
        check = dispute.CompletenessCheck(is_complete=False, missing_fields=["x"], rationale="?")
        with patch.object(dispute, "structured_step", AsyncMock(return_value=check)):
            update = await dispute.validate_completeness(state)

        self.assertTrue(update["completeness"].is_complete)
        self.assertEqual([], update["completeness"].missing_fields)

    def test_route_on_completeness_selects_both_branches(self):
        incomplete = {
            "completeness": dispute.CompletenessCheck(
                is_complete=False, missing_fields=["merchant"], rationale="r"
            )
        }
        complete = {
            "completeness": dispute.CompletenessCheck(
                is_complete=True, missing_fields=[], rationale="r"
            )
        }

        self.assertEqual("request_more_info", dispute.route_on_completeness(incomplete))
        self.assertEqual("assess_evidence", dispute.route_on_completeness(complete))

    async def test_request_more_info_still_requires_approval(self):
        state = {
            "request": _request("Dispute this charge."),
            "claim": dispute.DisputeClaim(),
            "completeness": dispute.CompletenessCheck(
                is_complete=False, missing_fields=["merchant"], rationale="r"
            ),
        }
        with patch.object(dispute, "structured_step", AsyncMock(return_value=None)):
            update = await dispute.request_more_info(state)

        result = update["result"]
        self.assertTrue(result.requires_approval)
        self.assertEqual("dispute_information_required", result.intent)
        self.assertEqual("request_approval", result.next_step)

    async def test_draft_plan_requires_approval_and_reports_model_mode(self):
        state = {
            "request": _request("Dispute the ACME charge of 42.00 on 2026-01-02, unauthorized."),
            "claim": dispute.DisputeClaim(
                merchant="ACME", amount="42.00", transaction_date="2026-01-02", reason="unauthorized"
            ),
            "completeness": dispute.CompletenessCheck(
                is_complete=True, missing_fields=[], rationale="r"
            ),
            "assessment": dispute.EvidenceAssessment(
                required_evidence=["Receipt"], eligibility_notes="n", strength="moderate"
            ),
        }
        narrative = dispute.DisputeNarrative(
            summary="Plan prepared.", recommended_action="Collect evidence.", evidence=["Receipt"]
        )
        with patch.object(dispute, "structured_step", AsyncMock(return_value=narrative)):
            update = await dispute.draft_plan(state)

        result = update["result"]
        self.assertTrue(result.requires_approval)
        self.assertEqual("dispute", result.intent)
        self.assertEqual("model", result.execution_mode)

    async def test_a_single_fallback_node_downgrades_execution_mode(self):
        state = {
            "request": _request("dispute"),
            "claim": dispute.DisputeClaim(),
            "completeness": dispute.CompletenessCheck(
                is_complete=True, missing_fields=[], rationale="r"
            ),
            "assessment": dispute.EvidenceAssessment(
                required_evidence=[], eligibility_notes="n", strength="weak"
            ),
            "used_fallback": True,
        }
        narrative = dispute.DisputeNarrative(summary="s", recommended_action="a")
        with patch.object(dispute, "structured_step", AsyncMock(return_value=narrative)):
            update = await dispute.draft_plan(state)

        self.assertEqual("fallback", update["result"].execution_mode)


class DisputeGraphBranchTests(unittest.IsolatedAsyncioTestCase):
    """Assert which branch runs, which node tests cannot show."""

    async def test_incomplete_claim_skips_evidence_assessment(self):
        async def stub(instructions, request, schema, step_context=None):
            if schema is dispute.DisputeClaim:
                return dispute.DisputeClaim()
            if schema is dispute.CompletenessCheck:
                return dispute.CompletenessCheck(is_complete=True, missing_fields=[], rationale="r")
            if schema is dispute.EvidenceAssessment:
                raise AssertionError("assess_evidence must not run for an incomplete claim")
            return dispute.DisputeNarrative(summary="s", recommended_action="a")

        with patch.object(dispute, "structured_step", stub):
            state = await dispute.graph.ainvoke({"request": _request("Dispute this charge.")})

        self.assertNotIn("assessment", state)
        self.assertEqual("dispute_information_required", state["result"].intent)
        self.assertTrue(state["result"].requires_approval)

    async def test_complete_claim_runs_evidence_assessment_then_drafts(self):
        async def stub(instructions, request, schema, step_context=None):
            if schema is dispute.DisputeClaim:
                return dispute.DisputeClaim(
                    merchant="ACME",
                    amount="42.00",
                    transaction_date="2026-01-02",
                    reason="unauthorized",
                )
            if schema is dispute.CompletenessCheck:
                return dispute.CompletenessCheck(is_complete=False, missing_fields=[], rationale="r")
            if schema is dispute.EvidenceAssessment:
                return dispute.EvidenceAssessment(
                    required_evidence=["Receipt"], eligibility_notes="n", strength="strong"
                )
            return dispute.DisputeNarrative(summary="s", recommended_action="a")

        with patch.object(dispute, "structured_step", stub):
            state = await dispute.graph.ainvoke(
                {"request": _request("Dispute the ACME charge of 42.00 on 2026-01-02.")}
            )

        self.assertIn("assessment", state)
        self.assertEqual("strong", state["assessment"].strength)
        self.assertEqual("dispute", state["result"].intent)


class SuspiciousNodeTests(unittest.IsolatedAsyncioTestCase):
    async def test_gather_signals_overrides_a_model_that_misses_an_action_request(self):
        # If a model overlooks "freeze", the approval gate would be skipped, so
        # the keyword check is OR-ed in rather than trusted to the model.
        missed = suspicious_activity.SignalSet(
            observed_facts=["Unrecognised charge"], hypotheses=[], action_requested=False
        )
        with patch.object(
            suspicious_activity, "structured_step", AsyncMock(return_value=missed)
        ):
            update = await suspicious_activity.gather_signals(
                {"request": _request("Freeze my card; this transaction is not mine.")}
            )

        self.assertTrue(update["signals"].action_requested)

    async def test_gather_signals_leaves_informational_requests_unflagged(self):
        signals = suspicious_activity.SignalSet(
            observed_facts=["Unrecognised charge"], hypotheses=[], action_requested=False
        )
        with patch.object(
            suspicious_activity, "structured_step", AsyncMock(return_value=signals)
        ):
            update = await suspicious_activity.gather_signals(
                {"request": _request("This transaction is not mine. Explain what I should review.")}
            )

        self.assertFalse(update["signals"].action_requested)

    async def test_gather_signals_without_model_still_detects_the_action(self):
        with patch.object(suspicious_activity, "structured_step", AsyncMock(return_value=None)):
            update = await suspicious_activity.gather_signals(
                {"request": _request("Please block my card.")}
            )

        self.assertTrue(update["signals"].action_requested)
        self.assertTrue(update["used_fallback"])

    def test_route_on_action_selects_both_branches(self):
        acting = {"signals": suspicious_activity.SignalSet(action_requested=True)}
        informational = {"signals": suspicious_activity.SignalSet(action_requested=False)}

        self.assertEqual("plan_protective_action", suspicious_activity.route_on_action(acting))
        self.assertEqual("explain_activity", suspicious_activity.route_on_action(informational))

    async def test_explain_activity_does_not_require_approval(self):
        state = {
            "request": _request("This transaction is not mine."),
            "signals": suspicious_activity.SignalSet(observed_facts=["Unrecognised charge"]),
            "classification": suspicious_activity.ActivityClassification(
                category="unauthorized_charge", severity="high", rationale="r"
            ),
        }
        with patch.object(suspicious_activity, "structured_step", AsyncMock(return_value=None)):
            update = await suspicious_activity.explain_activity(state)

        result = update["result"]
        self.assertFalse(result.requires_approval)
        self.assertEqual("respond_to_user", result.next_step)
        self.assertEqual("high", result.risk_level)
        self.assertEqual(["Unrecognised charge"], result.evidence)

    async def test_plan_protective_action_requires_approval(self):
        state = {
            "request": _request("Freeze my card."),
            "signals": suspicious_activity.SignalSet(action_requested=True),
            "classification": suspicious_activity.ActivityClassification(
                category="card_lost_or_stolen", severity="medium", rationale="r"
            ),
        }
        with patch.object(suspicious_activity, "structured_step", AsyncMock(return_value=None)):
            update = await suspicious_activity.plan_protective_action(state)

        result = update["result"]
        self.assertTrue(result.requires_approval)
        self.assertEqual("request_approval", result.next_step)
        self.assertEqual("medium", result.risk_level)


class SuspiciousGraphBranchTests(unittest.IsolatedAsyncioTestCase):
    async def _run(self, message: str):
        async def stub(instructions, request, schema, step_context=None):
            if schema is suspicious_activity.SignalSet:
                return suspicious_activity.SignalSet(
                    observed_facts=["Unrecognised charge"], action_requested=False
                )
            if schema is suspicious_activity.ActivityClassification:
                return suspicious_activity.ActivityClassification(
                    category="unauthorized_charge", severity="high", rationale="r"
                )
            return suspicious_activity.ActivityNarrative(summary="s", recommended_action="a")

        with patch.object(suspicious_activity, "structured_step", stub):
            return await suspicious_activity.graph.ainvoke({"request": _request(message)})

    async def test_informational_request_completes_without_approval(self):
        # Mirrors the smoke scenario that must end Completed, not WaitingForApproval.
        state = await self._run("This transaction is not mine. Explain what I should review.")

        self.assertFalse(state["result"].requires_approval)
        self.assertEqual("respond_to_user", state["result"].next_step)

    async def test_action_request_is_gated_behind_approval(self):
        state = await self._run("Freeze my card; this transaction is not mine.")

        self.assertTrue(state["result"].requires_approval)
        self.assertEqual("request_approval", state["result"].next_step)

    async def test_classification_severity_reaches_the_terminal_result(self):
        state = await self._run("This transaction is not mine.")

        self.assertEqual("high", state["classification"].severity)
        self.assertEqual("high", state["result"].risk_level)


class SingleNodeAgentsAreUnchangedTests(unittest.TestCase):
    """The two genuinely single-step agents keep the simple wrapper."""

    def test_planning_and_explanation_remain_single_node(self):
        from app.agents.registry import get_agent_graph

        for agent in (AgentName.WORKFLOW_PLANNING, AgentName.TRANSACTION_EXPLANATION):
            with self.subTest(agent=agent):
                nodes = set(get_agent_graph(agent).get_graph().nodes)
                self.assertEqual({"__start__", "analyze", "__end__"}, nodes)

    def test_multi_node_agents_have_the_documented_topology(self):
        from app.agents.registry import get_agent_graph

        self.assertEqual(
            {
                "__start__",
                "extract_claim",
                "validate_completeness",
                "assess_evidence",
                "request_more_info",
                "draft_plan",
                "__end__",
            },
            set(get_agent_graph(AgentName.DISPUTE_PLANNING).get_graph().nodes),
        )
        self.assertEqual(
            {
                "__start__",
                "gather_signals",
                "classify",
                "plan_protective_action",
                "explain_activity",
                "__end__",
            },
            set(get_agent_graph(AgentName.SUSPICIOUS_ACTIVITY).get_graph().nodes),
        )


if __name__ == "__main__":
    unittest.main()
