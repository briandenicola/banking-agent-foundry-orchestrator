"""Node-level and branch-level tests for the multi-node agent graphs.

These test node functions directly, with stubbed model output, so a node's
behaviour is verified independently of the full graph. Graph-level tests then
assert only the thing a node test cannot: which branch the conditional edge
selects.
"""

from __future__ import annotations

import unittest
from unittest.mock import AsyncMock, patch

from app.agents import dispute, planning, suspicious_activity, transaction_explanation
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


class PlanningNodeTests(unittest.IsolatedAsyncioTestCase):
    async def test_interpret_request_ors_the_action_keyword_with_the_model(self):
        # A model that overlooks "freeze my card" must not be able to route the
        # request past the approval gate.
        missed = planning.RequestInterpretation(asks_to_act=False)
        with patch.object(planning, "structured_step", AsyncMock(return_value=missed)):
            update = await planning.interpret_request(
                {"request": _request("Please freeze my card right now.")}
            )

        self.assertTrue(update["interpretation"].asks_to_act)

    async def test_interpret_request_ands_the_dispute_keyword_with_the_model(self):
        # The opposite direction: a model that infers a dispute from an
        # unrecognised charge must not be able to manufacture one.
        inferred = planning.RequestInterpretation(explicitly_requests_dispute=True)
        with patch.object(planning, "structured_step", AsyncMock(return_value=inferred)):
            update = await planning.interpret_request(
                {"request": _request("This charge is not mine.")}
            )

        self.assertFalse(update["interpretation"].explicitly_requests_dispute)

    async def test_interpret_request_keeps_an_explicit_dispute_request(self):
        asked = planning.RequestInterpretation(explicitly_requests_dispute=True)
        with patch.object(planning, "structured_step", AsyncMock(return_value=asked)):
            update = await planning.interpret_request(
                {"request": _request("I want to dispute this charge.")}
            )

        self.assertTrue(update["interpretation"].explicitly_requests_dispute)

    async def test_interpret_request_without_model_falls_back_to_keywords(self):
        with patch.object(planning, "structured_step", AsyncMock(return_value=None)):
            update = await planning.interpret_request(
                {"request": _request("Please close my account, this charge is not mine.")}
            )

        self.assertTrue(update["interpretation"].asks_to_act)
        self.assertTrue(update["interpretation"].reports_unrecognized_activity)
        self.assertFalse(update["interpretation"].explicitly_requests_dispute)
        self.assertTrue(update["used_fallback"])

    async def test_select_specialist_downgrades_an_unrequested_dispute(self):
        state = {
            "request": _request("This charge is not mine."),
            "interpretation": planning.RequestInterpretation(
                explicitly_requests_dispute=False, reports_unrecognized_activity=True
            ),
        }
        eager = planning.SpecialistSelection(
            selected_agent="dispute-planning",
            intent="dispute",
            risk_level="high",
            summary="Filing a dispute.",
        )
        with patch.object(planning, "structured_step", AsyncMock(return_value=eager)):
            update = await planning.select_specialist(state)

        self.assertEqual("suspicious-activity", update["selection"].selected_agent)
        self.assertEqual("suspicious_activity", update["selection"].intent)
        self.assertTrue(update["downgraded"])

    async def test_select_specialist_keeps_a_requested_dispute(self):
        state = {
            "request": _request("Please dispute this charge."),
            "interpretation": planning.RequestInterpretation(
                explicitly_requests_dispute=True
            ),
        }
        requested = planning.SpecialistSelection(
            selected_agent="dispute-planning",
            intent="dispute",
            risk_level="high",
            summary="Filing a dispute.",
        )
        with patch.object(planning, "structured_step", AsyncMock(return_value=requested)):
            update = await planning.select_specialist(state)

        self.assertEqual("dispute-planning", update["selection"].selected_agent)
        self.assertFalse(update["downgraded"])

    async def test_select_specialist_escalates_an_action_away_from_the_informational_agent(self):
        # transaction-explanation hard-codes requires_approval=False, so routing
        # an action request there would silently bypass the approval gate.
        state = {
            "request": _request("Please freeze my card."),
            "interpretation": planning.RequestInterpretation(asks_to_act=True),
        }
        informational = planning.SpecialistSelection(
            selected_agent="transaction-explanation",
            intent="transaction_explanation",
            risk_level="low",
            summary="Explaining a transaction.",
        )
        with patch.object(planning, "structured_step", AsyncMock(return_value=informational)):
            update = await planning.select_specialist(state)

        self.assertEqual("suspicious-activity", update["selection"].selected_agent)
        self.assertTrue(update["escalated"])

    async def test_select_specialist_without_model_sends_an_action_to_suspicious_activity(self):
        state = {
            "request": _request("Please freeze my card."),
            "interpretation": planning.RequestInterpretation(asks_to_act=True),
        }
        with patch.object(planning, "structured_step", AsyncMock(return_value=None)):
            update = await planning.select_specialist(state)

        self.assertEqual("suspicious-activity", update["selection"].selected_agent)
        self.assertTrue(update["used_fallback"])

    def test_route_on_action_gate_selects_both_branches(self):
        informational = {
            "interpretation": planning.RequestInterpretation(asks_to_act=False),
            "selection": planning.SpecialistSelection(
                selected_agent="suspicious-activity",
                intent="suspicious_activity",
                risk_level="high",
                summary="s",
            ),
        }
        acting = {
            "interpretation": planning.RequestInterpretation(asks_to_act=True),
            "selection": planning.SpecialistSelection(
                selected_agent="suspicious-activity",
                intent="suspicious_activity",
                risk_level="high",
                summary="s",
            ),
        }
        dispute_route = {
            "interpretation": planning.RequestInterpretation(asks_to_act=False),
            "selection": planning.SpecialistSelection(
                selected_agent="dispute-planning",
                intent="dispute",
                risk_level="high",
                summary="s",
            ),
        }

        # High risk with no action requested still needs no approval.
        self.assertEqual("route_informational", planning.route_on_action_gate(informational))
        self.assertEqual("gate_action_request", planning.route_on_action_gate(acting))
        # Initiating a dispute is action-taking even when the wording is calm.
        self.assertEqual("gate_action_request", planning.route_on_action_gate(dispute_route))

    async def test_downgrade_reason_reaches_the_audit_trail(self):
        state = {
            "request": _request("This charge is not mine."),
            "interpretation": planning.RequestInterpretation(),
            "selection": planning.SpecialistSelection(
                selected_agent="suspicious-activity",
                intent="suspicious_activity",
                risk_level="high",
                summary="s",
            ),
            "downgraded": True,
        }
        with patch.object(planning, "structured_step", AsyncMock(return_value=None)):
            update = await planning.route_informational(state)

        self.assertTrue(
            any("did not explicitly request a dispute" in item for item in update["result"].evidence)
        )

    async def test_terminal_nodes_own_the_approval_flag(self):
        state = {
            "request": _request("Freeze my card."),
            "interpretation": planning.RequestInterpretation(asks_to_act=True),
            "selection": planning.SpecialistSelection(
                selected_agent="suspicious-activity",
                intent="suspicious_activity",
                risk_level="high",
                summary="s",
            ),
        }
        with patch.object(planning, "structured_step", AsyncMock(return_value=None)):
            gated = await planning.gate_action_request(state)
            informational = await planning.route_informational(state)

        self.assertTrue(gated["result"].requires_approval)
        self.assertFalse(informational["result"].requires_approval)
        self.assertEqual("invoke_specialist", gated["result"].next_step)


class TransactionExplanationNodeTests(unittest.IsolatedAsyncioTestCase):
    async def test_extract_reference_records_only_stated_details(self):
        reference = transaction_explanation.TransactionReference(merchant="ACME")
        with patch.object(
            transaction_explanation, "structured_step", AsyncMock(return_value=reference)
        ):
            update = await transaction_explanation.extract_reference(
                {"request": _request("Why is my ACME charge pending?")}
            )

        self.assertEqual(["merchant"], update["reference"].identifying_details())
        self.assertNotIn("used_fallback", update)

    async def test_extract_reference_without_model_invents_nothing(self):
        with patch.object(
            transaction_explanation, "structured_step", AsyncMock(return_value=None)
        ):
            update = await transaction_explanation.extract_reference(
                {"request": _request("Why is this pending?")}
            )

        self.assertEqual([], update["reference"].identifying_details())
        self.assertTrue(update["used_fallback"])

    async def test_classify_status_without_model_admits_it_does_not_know(self):
        with patch.object(
            transaction_explanation, "structured_step", AsyncMock(return_value=None)
        ):
            update = await transaction_explanation.classify_status(
                {
                    "request": _request("Why is this pending?"),
                    "reference": transaction_explanation.TransactionReference(),
                }
            )

        self.assertEqual("unknown", update["assessment"].status)
        self.assertTrue(update["used_fallback"])

    def test_route_on_identifiability_selects_both_branches(self):
        identified = {
            "reference": transaction_explanation.TransactionReference(amount="42.00")
        }
        unidentified = {"reference": transaction_explanation.TransactionReference()}

        self.assertEqual(
            "explain_transaction",
            transaction_explanation.route_on_identifiability(identified),
        )
        self.assertEqual(
            "request_transaction_details",
            transaction_explanation.route_on_identifiability(unidentified),
        )
        self.assertEqual(
            "request_transaction_details",
            transaction_explanation.route_on_identifiability({}),
        )

    async def test_explanation_carries_identifying_details_into_evidence(self):
        state = {
            "request": _request("Why is my ACME charge pending?"),
            "reference": transaction_explanation.TransactionReference(
                merchant="ACME", amount="42.00"
            ),
        }
        with patch.object(
            transaction_explanation, "structured_step", AsyncMock(return_value=None)
        ):
            update = await transaction_explanation.explain_transaction(state)

        self.assertEqual(["merchant: ACME", "amount: 42.00"], update["result"].evidence)

    async def test_both_terminal_nodes_never_request_approval(self):
        state = {
            "request": _request("Why is my ACME charge pending?"),
            "reference": transaction_explanation.TransactionReference(merchant="ACME"),
        }
        with patch.object(
            transaction_explanation, "structured_step", AsyncMock(return_value=None)
        ):
            explained = await transaction_explanation.explain_transaction(state)
            asked = await transaction_explanation.request_transaction_details(state)

        for name, update in (("explain", explained), ("request_details", asked)):
            with self.subTest(node=name):
                self.assertFalse(update["result"].requires_approval)
                self.assertEqual("low", update["result"].risk_level)
                self.assertEqual("respond_to_user", update["result"].next_step)


class PlannerGatingInvariantTests(unittest.IsolatedAsyncioTestCase):
    """An action request must always be gated, whatever the model says.

    Regression guard: routing "freeze my card" to transaction-explanation --
    which hard-codes requires_approval=False -- would let an action request
    reach a terminal state with no approval gate.
    """

    async def test_action_requests_are_always_gated_and_never_informational(self):
        from app.agents.registry import get_agent_graph

        messages = (
            "Please freeze my card.",
            "Block my account now.",
            "Close this card, and explain why this charge is pending.",
        )
        graph = get_agent_graph(AgentName.WORKFLOW_PLANNING)

        for message in messages:
            with self.subTest(message=message):
                with patch.object(planning, "structured_step", AsyncMock(return_value=None)):
                    state = await graph.ainvoke({"request": _request(message)})

                result = state["result"]
                self.assertTrue(result.requires_approval)
                self.assertNotEqual(AgentName.TRANSACTION_EXPLANATION, result.selected_agent)


class AllAgentsAreMultiNodeTests(unittest.TestCase):
    """Every agent is a real multi-node graph with a conditional edge.

    This is the assertion that keeps the "multi-node LangGraph graphs" claim
    honest: a regression to the single-node ``analyze`` wrapper fails here.
    """

    def test_no_agent_uses_the_single_node_wrapper(self):
        from app.agents.registry import get_agent_graph

        for agent in AgentName:
            with self.subTest(agent=agent):
                nodes = set(get_agent_graph(agent).get_graph().nodes)
                self.assertNotIn("analyze", nodes)
                # __start__ and __end__ plus at least three real nodes.
                self.assertGreaterEqual(len(nodes), 5)

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
        self.assertEqual(
            {
                "__start__",
                "interpret_request",
                "select_specialist",
                "gate_action_request",
                "route_informational",
                "__end__",
            },
            set(get_agent_graph(AgentName.WORKFLOW_PLANNING).get_graph().nodes),
        )
        self.assertEqual(
            {
                "__start__",
                "extract_reference",
                "classify_status",
                "explain_transaction",
                "request_transaction_details",
                "__end__",
            },
            set(get_agent_graph(AgentName.TRANSACTION_EXPLANATION).get_graph().nodes),
        )


if __name__ == "__main__":
    unittest.main()
