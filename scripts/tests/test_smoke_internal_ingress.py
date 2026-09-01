"""Deterministic tests for smoke behaviour against internal orchestrator ingress.

The orchestrator runs on environment-internal ingress, so an operator
workstation cannot reach it.  Two smoke checks used to call it directly and
would simply start failing.  A check that fails for an expected reason is worse
than useless: it trains the operator to ignore red, and it silently stops
proving anything about the deployment's authentication posture.

Both checks were therefore reframed to assert the lockdown rather than to
assert reachability.  These tests verify that reframing, and -- importantly --
that the checks still FAIL if the orchestrator turns out to be publicly
reachable after all.  Without that direction, the checks would pass whether or
not the lockdown actually held.

No live smoke was run.  All HTTP interactions are stubbed; no Terraform, az
CLI, or Azure access is required.
"""
from __future__ import annotations

import importlib.util
import io
import sys
import types
import unittest
from pathlib import Path
from unittest.mock import patch
from urllib.error import HTTPError, URLError

_SCRIPT = Path(__file__).resolve().parents[1] / "smoke-mvp.py"


def _load_smoke() -> types.ModuleType:
    spec = importlib.util.spec_from_file_location("smoke_mvp", _SCRIPT)
    assert spec is not None and spec.loader is not None
    mod = importlib.util.module_from_spec(spec)
    sys.modules["smoke_mvp"] = mod
    spec.loader.exec_module(mod)  # type: ignore[union-attr]
    return mod


smoke = _load_smoke()
SmokeFailure = smoke.SmokeFailure

INTERNAL_URL = "https://app-orchestrator.internal.happyflower-1.swedencentral.azurecontainerapps.io"
EXTERNAL_URL = "https://app-orchestrator.happyflower-1.swedencentral.azurecontainerapps.io"
WEBUI_URL = "https://app-webui.happyflower-1.swedencentral.azurecontainerapps.io"


class _Response:
    """Minimal stand-in for the object urlopen returns as a context manager."""

    def __init__(self, status: int = 200, body: bytes = b"{}"):
        self.status = status
        self._body = body

    def read(self) -> bytes:
        return self._body

    def __enter__(self):
        return self

    def __exit__(self, *_args):
        return False


def _unreachable(*_args, **_kwargs):
    raise URLError("Name or service not known")


class IngressDetectionTests(unittest.TestCase):
    def test_internal_fqdn_is_recognised(self):
        self.assertTrue(smoke.orchestrator_is_internal(INTERNAL_URL))

    def test_external_fqdn_is_recognised(self):
        self.assertFalse(smoke.orchestrator_is_internal(EXTERNAL_URL))

    def test_detection_reads_the_hostname_not_the_path(self):
        # A path segment must not be mistaken for the internal-ingress marker.
        self.assertFalse(
            smoke.orchestrator_is_internal(f"{EXTERNAL_URL}/api/v1/.internal./x")
        )


class NotPubliclyReachableTests(unittest.TestCase):
    def test_connection_failure_is_the_success_condition(self):
        with patch.object(smoke, "urlopen", _unreachable):
            smoke.assert_not_publicly_reachable(INTERNAL_URL, 5, "Orchestrator")

    def test_a_successful_response_fails_the_check(self):
        # This is the direction that matters: if the lockdown did not hold, the
        # check must go red rather than quietly pass.
        with patch.object(smoke, "urlopen", lambda *a, **k: _Response(200)):
            with self.assertRaises(SmokeFailure) as caught:
                smoke.assert_not_publicly_reachable(INTERNAL_URL, 5, "Orchestrator")
        self.assertIn("public internet", str(caught.exception))

    def test_an_http_error_response_also_fails_the_check(self):
        # 401/500, and any 404 the app itself served, all mean something
        # answered, so the endpoint is exposed.
        def _http_error(*_args, **_kwargs):
            raise HTTPError(INTERNAL_URL, 404, "Not Found", {}, None)  # type: ignore[arg-type]

        with patch.object(smoke, "urlopen", _http_error):
            with self.assertRaises(SmokeFailure) as caught:
                smoke.assert_not_publicly_reachable(INTERNAL_URL, 5, "Orchestrator")
        self.assertIn("404", str(caught.exception))

    def test_the_platform_front_door_404_is_the_success_condition(self):
        """The environment serves internal and external apps behind one IP.

        The internal FQDN therefore resolves publicly, and the Container Apps
        front door answers with its own 404 page rather than routing to the
        app. That response proves the lockdown held, so treating it as a breach
        would fail every deployment that is correctly locked down.
        """
        page = (
            "<html><head><title>Azure Container App - Unavailable</title></head>"
            "<body>Error 404 - This Container App is stopped or does not exist."
            "</body></html>"
        ).encode()

        def _front_door_404(*_args, **_kwargs):
            raise HTTPError(
                INTERNAL_URL, 404, "Not Found", {}, io.BytesIO(page)  # type: ignore[arg-type]
            )

        with patch.object(smoke, "urlopen", _front_door_404):
            smoke.assert_not_publicly_reachable(INTERNAL_URL, 5, "Orchestrator")

    def test_an_app_served_404_still_fails_the_check(self):
        # Without the marker the 404 came from the app, which means it answered.
        def _app_404(*_args, **_kwargs):
            raise HTTPError(
                INTERNAL_URL, 404, "Not Found", {}, io.BytesIO(b'{"error":"no route"}')  # type: ignore[arg-type]
            )

        with patch.object(smoke, "urlopen", _app_404):
            with self.assertRaises(SmokeFailure) as caught:
                smoke.assert_not_publicly_reachable(INTERNAL_URL, 5, "Orchestrator")
        self.assertIn("404", str(caught.exception))

    def test_a_non_404_carrying_the_marker_still_fails_the_check(self):
        # The marker only excuses a 404. Anything else answered.
        def _marked_500(*_args, **_kwargs):
            raise HTTPError(
                INTERNAL_URL,
                500,
                "Server Error",
                {},
                io.BytesIO(b"This Container App is stopped or does not exist"),  # type: ignore[arg-type]
            )

        with patch.object(smoke, "urlopen", _marked_500):
            with self.assertRaises(SmokeFailure) as caught:
                smoke.assert_not_publicly_reachable(INTERNAL_URL, 5, "Orchestrator")
        self.assertIn("500", str(caught.exception))


class HealthCheckTests(unittest.TestCase):
    def test_internal_ingress_verifies_health_through_the_webui(self):
        calls: list[str] = []

        def _urlopen(request, *_args, **_kwargs):
            url = request.full_url if hasattr(request, "full_url") else str(request)
            calls.append(url)
            if ".internal." in url:
                raise URLError("Name or service not known")
            return _Response(200)

        with patch.object(smoke, "urlopen", _urlopen):
            details = smoke.check_health(INTERNAL_URL, WEBUI_URL, 5)

        self.assertEqual("internal", details["ingress"])
        self.assertFalse(details["reachable_from_internet"])
        self.assertEqual(200, details["orchestrator_readiness_via_webui"])
        self.assertTrue(any(WEBUI_URL in call for call in calls))

    def test_internal_ingress_fails_when_the_webui_cannot_confirm_health(self):
        # Transitive coverage must be real: an unhealthy orchestrator makes the
        # Web UI readiness probe fail, and that must surface.
        def _urlopen(request, *_args, **_kwargs):
            url = request.full_url if hasattr(request, "full_url") else str(request)
            if ".internal." in url:
                raise URLError("Name or service not known")
            raise HTTPError(url, 503, "Service Unavailable", {}, None)  # type: ignore[arg-type]

        with patch.object(smoke, "urlopen", _urlopen):
            with self.assertRaises(SmokeFailure):
                smoke.check_health(INTERNAL_URL, WEBUI_URL, 5)

    def test_external_ingress_still_probes_the_orchestrator_directly(self):
        calls: list[str] = []

        def _urlopen(request, *_args, **_kwargs):
            calls.append(request.full_url)
            return _Response(200)

        with patch.object(smoke, "urlopen", _urlopen):
            details = smoke.check_health(EXTERNAL_URL, WEBUI_URL, 5)

        self.assertEqual("external", details["ingress"])
        self.assertEqual(200, details["liveness"])
        self.assertEqual(200, details["readiness"])
        self.assertTrue(any(call.endswith("/health/live") for call in calls))


class AuthenticationBaselineTests(unittest.TestCase):
    def test_internal_ingress_reports_the_residual_exposure(self):
        with patch.object(smoke, "urlopen", _unreachable):
            details = smoke.check_authentication_baseline(INTERNAL_URL, 5, False)

        self.assertEqual("internal-ingress", details["control"])
        self.assertFalse(details["anonymous_reachable_from_internet"])
        # Internal ingress is not authentication, and the evidence must say so.
        self.assertFalse(details["authentication_required"])
        self.assertIn("Web UI remains public", details["residual_exposure"])

    def test_internal_ingress_fails_if_the_approval_endpoint_answers(self):
        with patch.object(smoke, "urlopen", lambda *a, **k: _Response(200)):
            with self.assertRaises(SmokeFailure):
                smoke.check_authentication_baseline(INTERNAL_URL, 5, False)

    def test_external_ingress_keeps_the_original_anonymous_probe(self):
        with patch.object(smoke, "request_json", lambda *a, **k: (404, "{}")):
            details = smoke.check_authentication_baseline(EXTERNAL_URL, 5, False)

        self.assertEqual("public-ingress", details["control"])
        self.assertEqual(404, details["anonymous_http_status"])
        self.assertFalse(details["authentication_required"])

    def test_external_ingress_still_enforces_401_when_auth_is_expected(self):
        with patch.object(smoke, "request_json", lambda *a, **k: (200, "{}")):
            with self.assertRaises(SmokeFailure):
                smoke.check_authentication_baseline(EXTERNAL_URL, 5, True)


class ContainerAppNameDerivationTests(unittest.TestCase):
    """The revision check derives the Container App name from the URL.

    Internal FQDNs gain an extra ".internal" label, so this guards against the
    derivation silently picking up the wrong name after the ingress change.
    """

    def test_app_name_is_derived_from_an_internal_fqdn(self):
        captured: list[str] = []

        class _Completed:
            stdout = (
                '{"latestRevisionName":"r1","latestReadyRevisionName":"r1",'
                '"runningStatus":"Running"}'
            )

        def _run(cmd, **_kwargs):
            captured.append(cmd[cmd.index("--name") + 1])
            return _Completed()

        with patch.object(smoke.subprocess, "run", _run):
            revisions = smoke.check_container_app_revisions("rg", (INTERNAL_URL,))

        self.assertEqual(["app-orchestrator"], captured)
        self.assertIn("app-orchestrator", revisions)


if __name__ == "__main__":
    unittest.main()
