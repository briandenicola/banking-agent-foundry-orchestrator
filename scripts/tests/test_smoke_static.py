"""Deterministic static tests for scripts/smoke-mvp.py.

These tests exercise the smoke script's logic without connecting to any live
endpoint.  All HTTP interactions are replaced by simple stubs.  The tests
verify the async contract behaviour added in the aria-async-workflow ADR:

  - POST must return 202; any other code raises SmokeFailure.
  - poll_workflow stops on each of the four TERMINAL_STATES.
  - poll_workflow raises SmokeFailure with diagnostic context on timeout.
  - poll_workflow raises SmokeFailure on unexpected HTTP error codes.
  - check_workflows raises SmokeFailure when the polled status != expected.
  - TERMINAL_STATES contains exactly the four states defined by the ADR.
  - The --poll-timeout argument is accepted and defaults to
    DEFAULT_POLL_TIMEOUT_SECONDS (or SMOKE_POLL_TIMEOUT_SECONDS env var).

No live smoke was run.  All infrastructure dependencies (Terraform, az CLI,
Azure Container Apps) are absent from the test runner.
"""
from __future__ import annotations

import importlib.util
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch

# ---------------------------------------------------------------------------
# Load the smoke script as a module without executing main()
# ---------------------------------------------------------------------------
_SCRIPT = Path(__file__).resolve().parents[1] / "smoke-mvp.py"


def _load_smoke() -> types.ModuleType:
    spec = importlib.util.spec_from_file_location("smoke_mvp", _SCRIPT)
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    sys.modules["smoke_mvp"] = mod  # required for dataclass __module__ resolution
    spec.loader.exec_module(mod)  # type: ignore[union-attr]
    return mod


smoke = _load_smoke()

SmokeFailure = smoke.SmokeFailure
TERMINAL_STATES = smoke.TERMINAL_STATES
DEFAULT_POLL_TIMEOUT_SECONDS = smoke.DEFAULT_POLL_TIMEOUT_SECONDS
DEFAULT_POLL_INITIAL_INTERVAL = smoke.DEFAULT_POLL_INITIAL_INTERVAL
DEFAULT_POLL_MAX_INTERVAL = smoke.DEFAULT_POLL_MAX_INTERVAL
DEFAULT_POLL_BACKOFF_FACTOR = smoke.DEFAULT_POLL_BACKOFF_FACTOR


# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------
def _make_stub(*responses):
    it = iter(responses)
    def _stub(*_args, **_kwargs):
        return next(it)
    return _stub


def _model_events():
    details = '{"contract_version":"1.0","execution_mode":"model"}'
    return [
        {"type": "workflow.plan", "details": details},
        {"type": "mcp.invoked", "details": details},
    ]


# ---------------------------------------------------------------------------
# Test: TERMINAL_STATES completeness
# ---------------------------------------------------------------------------
class TestTerminalStates(unittest.TestCase):
    def test_contains_exactly_adr_states(self):
        expected = {"Completed", "Failed", "Rejected", "WaitingForApproval"}
        self.assertEqual(TERMINAL_STATES, expected)

    def test_is_frozenset(self):
        self.assertIsInstance(TERMINAL_STATES, frozenset)

    def test_draft_not_terminal(self):
        self.assertNotIn("Draft", TERMINAL_STATES)

    def test_recovering_not_terminal(self):
        self.assertNotIn("Recovering", TERMINAL_STATES)


# ---------------------------------------------------------------------------
# Test: start_workflow -- async 202 contract
# ---------------------------------------------------------------------------
class TestStartWorkflow(unittest.TestCase):
    def _call(self, http_status: int, body: dict):
        stub = _make_stub((http_status, body))
        with patch.object(smoke, "request_json", stub):
            return smoke.start_workflow("http://api", "test message", 10, None)

    def test_202_returns_body(self):
        body = {"workflowId": "abc", "traceId": "t1", "status": "Draft", "message": "ok"}
        result = self._call(202, body)
        self.assertEqual(result["workflowId"], "abc")
        self.assertEqual(result["status"], "Draft")

    def test_200_raises_smoke_failure(self):
        """202 is required; the old 200 synchronous response must now be rejected."""
        with self.assertRaises(SmokeFailure) as ctx:
            self._call(200, {"workflowId": "x", "traceId": "y", "status": "Completed"})
        self.assertIn("202", str(ctx.exception))

    def test_500_raises_smoke_failure(self):
        with self.assertRaises(SmokeFailure):
            self._call(500, {"detail": "internal error"})

    def test_missing_workflow_id_raises(self):
        with self.assertRaises(SmokeFailure) as ctx:
            self._call(202, {"traceId": "t", "status": "Draft"})
        self.assertIn("workflowId", str(ctx.exception))

    def test_missing_trace_id_raises(self):
        with self.assertRaises(SmokeFailure) as ctx:
            self._call(202, {"workflowId": "w", "status": "Draft"})
        self.assertIn("traceId", str(ctx.exception))


# ---------------------------------------------------------------------------
# Test: poll_workflow -- terminal state detection and failure cases
# ---------------------------------------------------------------------------
class TestPollWorkflow(unittest.TestCase):
    def _call(self, responses, poll_timeout: int = 30):
        stub = _make_stub(*responses)
        with patch.object(smoke, "request_json", stub), patch("time.sleep"):
            return smoke.poll_workflow("http://api", "wf-1", 5, poll_timeout, None)

    def test_stops_on_completed(self):
        result = self._call([(200, {"status": "Completed", "events": []})])
        self.assertEqual(result["status"], "Completed")
        self.assertEqual(result["poll_attempts"], 1)

    def test_stops_on_failed(self):
        result = self._call([(200, {"status": "Failed", "events": []})])
        self.assertEqual(result["status"], "Failed")

    def test_stops_on_rejected(self):
        result = self._call([(200, {"status": "Rejected", "events": []})])
        self.assertEqual(result["status"], "Rejected")

    def test_stops_on_waiting_for_approval(self):
        result = self._call([(200, {"status": "WaitingForApproval", "events": []})])
        self.assertEqual(result["status"], "WaitingForApproval")

    def test_retries_before_reaching_terminal(self):
        result = self._call([
            (200, {"status": "Draft", "events": []}),
            (200, {"status": "Recovering", "events": []}),
            (200, {"status": "Completed", "events": [{"type": "routed"}]}),
        ])
        self.assertEqual(result["status"], "Completed")
        self.assertEqual(result["poll_attempts"], 3)

    def test_includes_events_in_result(self):
        events = [{"type": "planned"}, {"type": "routed"}]
        result = self._call([(200, {"status": "Completed", "events": events})])
        self.assertEqual(result["events"], events)

    def test_unexpected_http_raises(self):
        with self.assertRaises(SmokeFailure) as ctx:
            self._call([(500, {"detail": "boom"})])
        self.assertIn("500", str(ctx.exception))
        self.assertIn("wf-1", str(ctx.exception))

    def test_timeout_raises_with_last_status_and_timeline(self):
        """Timeout must surface the last-known status and event timeline."""
        responses = iter([(200, {"status": "Draft", "events": [{"t": "e1"}]})])
        call_n = [0]
        base = __import__("time").monotonic()

        def _fake_monotonic():
            call_n[0] += 1
            # First 2 calls: before deadline; rest: after deadline
            return base if call_n[0] <= 2 else base + 9999

        def _stub(*_args, **_kwargs):
            return next(responses)

        with patch.object(smoke, "request_json", _stub), \
             patch("time.sleep"), \
             patch("time.monotonic", _fake_monotonic):
            with self.assertRaises(SmokeFailure) as ctx:
                smoke.poll_workflow("http://api", "wf-timeout", 5, 0, None)

        err = str(ctx.exception)
        self.assertIn("wf-timeout", err)
        self.assertIn("Draft", err)

    def test_never_synthesises_success(self):
        """poll_workflow must raise, not return, when no terminal state is reached."""
        # Provide one response, then make time.monotonic() report past-deadline
        # on the very next remaining-time check so the loop exits without a
        # second request_json call.
        responses = iter([(200, {"status": "Recovering", "events": []})])
        call_n = [0]
        base = __import__("time").monotonic()

        def _fake_monotonic():
            call_n[0] += 1
            # Call 1: deadline setup (base + poll_timeout).
            # Call 2+: remaining check after first response — return far future
            # so remaining < 0 → break without another request.
            return base if call_n[0] <= 1 else base + 9999

        def _stub(*_args, **_kwargs):
            return next(responses)

        with patch.object(smoke, "request_json", _stub), \
             patch("time.sleep"), \
             patch("time.monotonic", _fake_monotonic):
            with self.assertRaises(SmokeFailure) as ctx:
                smoke.poll_workflow("http://api", "wf-synth", 5, 1, None)

        self.assertIn("Recovering", str(ctx.exception))


# ---------------------------------------------------------------------------
# Test: check_workflows -- 202 + poll integration
# ---------------------------------------------------------------------------
class TestCheckWorkflows(unittest.TestCase):
    _SCENARIO_STATUSES = ["Completed", "Completed", "WaitingForApproval", "WaitingForApproval"]

    def _build_responses(self):
        resps = []
        for i, st in enumerate(self._SCENARIO_STATUSES):
            resps.append((202, {"workflowId": f"wf-{i}", "traceId": "t", "status": "Draft"}))
            resps.append((200, {"status": st, "events": _model_events()}))
        # approval POST
        resps.append((200, {"workflowId": "wf-3", "status": "Completed", "traceId": "t"}))
        # two check_workflow_get_state GETs
        resps.append((200, {"status": "Completed", "events": []}))
        resps.append((200, {"status": "Completed", "events": []}))
        return iter(resps)

    def test_success_path(self):
        resps = self._build_responses()
        with patch.object(smoke, "request_json", lambda *a, **k: next(resps)), \
             patch("time.sleep"):
            result = smoke.check_workflows("http://api", 5, 30, "tok")

        self.assertIn("scenarios", result)
        self.assertIn("approval", result)
        self.assertEqual(result["approval"]["status"], "Completed")
        for name, info in result["scenarios"].items():
            self.assertIn("poll_attempts", info)
            self.assertEqual(info["agent_execution_modes"], ["model", "model"])

    def test_wrong_terminal_status_raises(self):
        resps = iter([
            (202, {"workflowId": "wf-x", "traceId": "t", "status": "Draft"}),
            (200, {"status": "Failed", "events": [{"t": "fail-event"}]}),
        ])
        with patch.object(smoke, "request_json", lambda *a, **k: next(resps)), \
             patch("time.sleep"):
            with self.assertRaises(SmokeFailure) as ctx:
                smoke.check_workflows("http://api", 5, 30, "tok")

        err = str(ctx.exception)
        self.assertIn("transaction-information", err)
        self.assertIn("Completed", err)
        self.assertIn("Failed", err)

    def test_scenario_name_included_in_failure_message(self):
        resps = iter([
            (202, {"workflowId": "wf-y", "traceId": "t", "status": "Draft"}),
            (500, {"detail": "crash"}),
        ])
        with patch.object(smoke, "request_json", lambda *a, **k: next(resps)), \
             patch("time.sleep"):
            with self.assertRaises(SmokeFailure) as ctx:
                smoke.check_workflows("http://api", 5, 30, "tok")

        self.assertIn("transaction-information", str(ctx.exception))


class TestRequireLiveModelExecution(unittest.TestCase):
    def test_accepts_planner_and_specialist_model_execution(self):
        self.assertEqual(
            smoke.require_live_model_execution(_model_events(), "demo"),
            ["model", "model"],
        )

    def test_rejects_fallback_execution(self):
        events = _model_events()
        events[1]["details"] = '{"contract_version":"1.0","execution_mode":"fallback"}'
        with self.assertRaises(SmokeFailure) as ctx:
            smoke.require_live_model_execution(events, "demo")
        self.assertIn("non-model", str(ctx.exception))

    def test_rejects_missing_execution_evidence(self):
        with self.assertRaises(SmokeFailure) as ctx:
            smoke.require_live_model_execution([], "demo")
        self.assertIn("planner and specialist", str(ctx.exception))

    def test_rejects_duplicate_specialist_events_without_planner(self):
        events = _model_events()
        events[0]["type"] = "mcp.invoked"
        with self.assertRaises(SmokeFailure) as ctx:
            smoke.require_live_model_execution(events, "demo")
        self.assertIn("workflow.plan", str(ctx.exception))


# ---------------------------------------------------------------------------
# Test: CLI --poll-timeout argument
# ---------------------------------------------------------------------------
class TestParseArgs(unittest.TestCase):
    def _parse(self, *args):
        with patch("sys.argv", ["smoke-mvp.py"] + list(args)):
            return smoke.parse_args()

    def test_poll_timeout_default(self):
        with patch.dict("os.environ", {}, clear=False):
            # Remove override if present
            import os; os.environ.pop("SMOKE_POLL_TIMEOUT_SECONDS", None)
            args = self._parse()
        self.assertEqual(args.poll_timeout, DEFAULT_POLL_TIMEOUT_SECONDS)

    def test_poll_timeout_explicit(self):
        args = self._parse("--poll-timeout", "45")
        self.assertEqual(args.poll_timeout, 45)

    def test_poll_timeout_env_override(self):
        with patch.dict("os.environ", {"SMOKE_POLL_TIMEOUT_SECONDS": "120"}):
            args = self._parse()
        self.assertEqual(args.poll_timeout, 120)

    def test_timeout_and_output_still_present(self):
        args = self._parse("--timeout", "30")
        self.assertEqual(args.timeout, 30)
        self.assertIsNone(args.output)


if __name__ == "__main__":
    unittest.main()


class TestWebuiRequiresSignin(unittest.TestCase):
    """The sign-in probe decides whether two checks may stand down.

    It must recognise authentication, and must not mistake a broken Web UI for
    a protected one, or the smoke run would report success while covering
    nothing.
    """

    def _probe(self, opener):
        with patch.object(smoke, "urlopen", opener):
            return smoke.webui_requires_signin("https://webui.test", 5)

    def test_401_means_authentication_is_enabled(self):
        from urllib.error import HTTPError

        def opener(request, timeout=None):
            raise HTTPError("https://webui.test/", 401, "Unauthorized", {}, None)

        self.assertTrue(self._probe(opener))

    def test_redirect_to_entra_means_authentication_is_enabled(self):
        class Response:
            headers = {"Location": "https://login.microsoftonline.com/common/oauth2/v2.0/authorize"}

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        self.assertTrue(self._probe(lambda request, timeout=None: Response()))

    def test_a_server_error_is_not_treated_as_authentication(self):
        """A 500 must fail the run, not excuse it."""
        from urllib.error import HTTPError

        def opener(request, timeout=None):
            raise HTTPError("https://webui.test/", 500, "Server Error", {}, None)

        self.assertFalse(self._probe(opener))

    def test_an_unreachable_webui_is_not_treated_as_authentication(self):
        def opener(request, timeout=None):
            raise OSError("connection refused")

        self.assertFalse(self._probe(opener))

    def test_a_healthy_anonymous_webui_is_not_treated_as_authentication(self):
        class Response:
            headers: dict[str, str] = {}

            def __enter__(self):
                return self

            def __exit__(self, *args):
                return False

        self.assertFalse(self._probe(lambda request, timeout=None: Response()))


class TestSkippedCheck(unittest.TestCase):
    def test_a_skipped_check_is_marked_and_carries_a_reason(self):
        """Skipped must never be indistinguishable from passed in the evidence."""
        result = smoke.skipped_check("webui-form", "sign-in required")

        self.assertTrue(result.skipped)
        self.assertEqual("sign-in required", result.details["skipped"])

    def test_a_real_check_is_not_marked_skipped(self):
        result = smoke.run_check("something", lambda: {"ok": True})

        self.assertFalse(result.skipped)
        self.assertTrue(result.passed)
