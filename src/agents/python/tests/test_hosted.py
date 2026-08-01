"""
Tests for app/hosted.py — the InvocationAgentServerHost boundary.

All tests exercise the actual POST /invocations HTTP boundary via
httpx.AsyncClient + ASGITransport; no live network or Azure calls are made.
The module-level `graph` object is replaced with an AsyncMock per test,
avoiding import-time model calls.
"""
from __future__ import annotations

import asyncio
import os
import unittest
from unittest.mock import AsyncMock, MagicMock, patch

import httpx

os.environ["OTEL_SDK_DISABLED"] = "true"

import app.hosted as hosted_module
from app.contracts import AgentName, AgentRequest, AgentResult

# ---------------------------------------------------------------------------
# Helpers
# ---------------------------------------------------------------------------

_VALID_RESULT = AgentResult(
    agent=AgentName.WORKFLOW_PLANNING,
    status="ok",
    trace_id="t-1234",
    intent="workflow_planning",
    summary="Routed to transaction explanation.",
    risk_level="low",
    requires_approval=False,
    recommended_action="Explain the transaction.",
    next_step="respond_to_user",
)

_VALID_PAYLOAD = {"message": "Why is this card transaction pending?", "trace_id": "t-1234"}


def _make_graph_mock(result: AgentResult | None = None, *, side_effect=None) -> AsyncMock:
    """Return a graph-like AsyncMock whose ainvoke returns {'result': result}."""
    mock = MagicMock()
    if side_effect is not None:
        mock.ainvoke = AsyncMock(side_effect=side_effect)
    else:
        mock.ainvoke = AsyncMock(return_value={"result": result or _VALID_RESULT})
    return mock


# ---------------------------------------------------------------------------
# Test cases
# ---------------------------------------------------------------------------


class HostedInvocationTests(unittest.IsolatedAsyncioTestCase):
    """End-to-end boundary tests driven via the real ASGI app."""

    async def _post(self, payload: dict) -> httpx.Response:
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=hosted_module.app),
            base_url="http://test",
        ) as client:
            return await client.post("/invocations", json=payload)

    async def _post_raw(self, payload: bytes) -> httpx.Response:
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=hosted_module.app),
            base_url="http://test",
        ) as client:
            return await client.post(
                "/invocations",
                content=payload,
                headers={"content-type": "application/json"},
            )

    # ------------------------------------------------------------------
    # 1. Valid success response
    # ------------------------------------------------------------------

    async def test_valid_request_returns_200_with_agent_result_schema(self):
        """A well-formed request with a healthy graph produces a 200 whose body
        satisfies the full AgentResult schema."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertEqual(200, resp.status_code)
        body = resp.json()
        # Validate every required AgentResult field is present
        result = AgentResult.model_validate(body)
        self.assertEqual("ok", result.status)
        self.assertEqual("t-1234", result.trace_id)
        self.assertEqual(AgentName.WORKFLOW_PLANNING, result.agent)

    # ------------------------------------------------------------------
    # 2. Deterministic graph / agent failure
    # ------------------------------------------------------------------

    async def test_graph_runtime_error_returns_500(self):
        """An unhandled exception inside graph.ainvoke must yield 500, not an
        unmasked ASGI crash."""
        broken_graph = _make_graph_mock(side_effect=RuntimeError("LangGraph node blew up"))
        with patch.object(hosted_module, "graph", broken_graph):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertEqual(500, resp.status_code)
        body = resp.json()
        self.assertEqual("agent_error", body["error"])
        self.assertEqual("Agent invocation failed.", body["detail"])

    async def test_graph_value_error_returns_500_without_internal_detail(self):
        """ValueError from graph yields a safe 500 response."""
        broken_graph = _make_graph_mock(side_effect=ValueError("unexpected state shape"))
        with patch.object(hosted_module, "graph", broken_graph):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertEqual(500, resp.status_code)
        self.assertNotIn("unexpected state shape", resp.json()["detail"])

    # ------------------------------------------------------------------
    # 3. Timeout / deadline behavior
    # ------------------------------------------------------------------

    async def _never_return(*_args, **_kwargs):
        await asyncio.sleep(9999)

    async def test_timeout_returns_504(self):
        """When graph.ainvoke hangs past the deadline the handler must return 504."""
        hung_graph = _make_graph_mock(side_effect=HostedInvocationTests._never_return)
        with (
            patch.object(hosted_module, "graph", hung_graph),
            patch.object(hosted_module, "_INVOKE_TIMEOUT", 0.05),
        ):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertEqual(504, resp.status_code)
        body = resp.json()
        self.assertEqual("timeout", body["error"])
        self.assertIn("0.05", body["detail"])

    async def test_short_timeout_does_not_return_partial_state(self):
        """504 response must never contain a partial result key."""
        hung_graph = _make_graph_mock(side_effect=HostedInvocationTests._never_return)
        with (
            patch.object(hosted_module, "graph", hung_graph),
            patch.object(hosted_module, "_INVOKE_TIMEOUT", 0.05),
        ):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertNotIn("result", resp.json())
        self.assertNotIn("agent", resp.json())

    # ------------------------------------------------------------------
    # 4. Malformed / invalid request boundary
    # ------------------------------------------------------------------

    async def test_missing_message_field_returns_400(self):
        """Payload missing the required `message` field must yield 400."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post({"trace_id": "t-xyz"})

        self.assertEqual(400, resp.status_code)
        body = resp.json()
        self.assertEqual("invalid_request", body["error"])
        self.assertEqual("Request payload is invalid.", body["detail"])

    async def test_empty_message_returns_400(self):
        """`message` with min_length=1 cannot be an empty string."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post({"message": "", "trace_id": "t-xyz"})

        self.assertEqual(400, resp.status_code)
        self.assertEqual("invalid_request", resp.json()["error"])

    async def test_non_object_payload_returns_400(self):
        """A JSON array at the root is not a valid AgentRequest object."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post([])  # type: ignore[arg-type]

        self.assertEqual(400, resp.status_code)

    async def test_malformed_json_returns_safe_400(self):
        resp = await self._post_raw(b'{"message":')

        self.assertEqual(400, resp.status_code)
        self.assertEqual(
            {"error": "invalid_request", "detail": "Request payload is invalid."},
            resp.json(),
        )

    async def test_invalid_utf8_returns_safe_400(self):
        resp = await self._post_raw(b'{"message":"\xff"}')

        self.assertEqual(400, resp.status_code)
        self.assertEqual("invalid_request", resp.json()["error"])

    # ------------------------------------------------------------------
    # 5. Response contract / schema
    # ------------------------------------------------------------------

    async def test_response_content_type_is_json(self):
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post(_VALID_PAYLOAD)
        self.assertIn("application/json", resp.headers["content-type"])

    async def test_response_trace_id_propagated_from_request(self):
        """The trace_id in the response must originate from the graph result,
        which in production echoes the request trace_id through."""
        result = AgentResult(
            agent=AgentName.TRANSACTION_EXPLANATION,
            status="ok",
            trace_id="request-trace-abc",
            intent="transaction_explanation",
            summary="ok",
            risk_level="low",
            requires_approval=False,
            recommended_action="none",
            next_step="respond_to_user",
        )
        graph = _make_graph_mock(result)
        with patch.object(hosted_module, "graph", graph):
            resp = await self._post({"message": "explain tx", "trace_id": "request-trace-abc"})

        self.assertEqual(200, resp.status_code)
        self.assertEqual("request-trace-abc", resp.json()["trace_id"])
        request = graph.ainvoke.await_args.args[0]["request"]
        self.assertEqual("request-trace-abc", request.trace_id)

    async def test_error_response_body_never_leaks_stack_trace(self):
        """The 500 response must not expose exception messages or tracebacks."""
        broken_graph = _make_graph_mock(side_effect=RuntimeError("controlled failure"))
        with patch.object(hosted_module, "graph", broken_graph):
            resp = await self._post(_VALID_PAYLOAD)

        detail = resp.json()["detail"]
        self.assertNotIn("controlled failure", detail)
        self.assertNotIn("Traceback", detail)
        self.assertNotIn("File ", detail)

    async def test_success_response_requires_approval_field_present(self):
        """requires_approval must always be present in a successful response."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertIn("requires_approval", resp.json())

    async def test_malformed_graph_state_returns_safe_500(self):
        graph = MagicMock()
        graph.ainvoke = AsyncMock(return_value={})
        with patch.object(hosted_module, "graph", graph):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertEqual(500, resp.status_code)
        self.assertEqual(
            {"error": "agent_error", "detail": "Agent invocation failed."},
            resp.json(),
        )

    async def test_success_response_risk_level_is_valid_enum(self):
        """risk_level must be one of: low, medium, high."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post(_VALID_PAYLOAD)

        self.assertIn(resp.json()["risk_level"], ("low", "medium", "high"))


if __name__ == "__main__":
    unittest.main()


class HostedContractVersionTests(unittest.IsolatedAsyncioTestCase):
    """Verify that AgentResult contract fields appear in hosted responses."""

    async def _post(self, payload: dict) -> httpx.Response:
        async with httpx.AsyncClient(
            transport=httpx.ASGITransport(app=hosted_module.app),
            base_url="http://test",
        ) as client:
            return await client.post("/invocations", json=payload)

    async def test_response_includes_contract_version(self):
        """Successful response must include contract_version field."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post(_VALID_PAYLOAD)
        self.assertEqual(200, resp.status_code)
        self.assertIn("contract_version", resp.json())
        self.assertEqual("1.0", resp.json()["contract_version"])

    async def test_response_includes_execution_mode(self):
        """Successful response must include execution_mode field."""
        with patch.object(hosted_module, "graph", _make_graph_mock()):
            resp = await self._post(_VALID_PAYLOAD)
        self.assertEqual(200, resp.status_code)
        self.assertIn("execution_mode", resp.json())
        self.assertIn(resp.json()["execution_mode"], ("model", "fallback"))

    async def test_model_execution_mode_propagated(self):
        """When a result has execution_mode=model it propagates to the client."""
        model_result = AgentResult(
            agent=AgentName.WORKFLOW_PLANNING,
            status="ok",
            trace_id="t-1234",
            execution_mode="model",
            intent="workflow_planning",
            summary="Routed.",
            risk_level="low",
            requires_approval=False,
            recommended_action="Explain.",
            next_step="respond_to_user",
        )
        with patch.object(hosted_module, "graph", _make_graph_mock(model_result)):
            resp = await self._post(_VALID_PAYLOAD)
        self.assertEqual("model", resp.json()["execution_mode"])
