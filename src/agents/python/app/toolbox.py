"""Foundry Toolbox integration for hosted LangGraph agents.

A hosted agent's definition has no declarative ``tools`` array — that is a
prompt-agent feature. Foundry instead exposes managed tools to hosted agents
through a **toolbox**: a single MCP-compatible endpoint that the container
calls at runtime, authenticated with the agent's own Entra identity.

This module is deliberately opt-in. When ``BANKING_AGENT_TOOLBOX_NAME`` is
unset the agents behave exactly as they did before, so an environment without
a toolbox keeps working.

The tool loop here is a single round: the model may request tool calls, we
execute them, and the observations are handed back to the caller as evidence
strings. It is not an open-ended agent loop, because tool output feeds an
informational explanation rather than any approval decision.
"""

from __future__ import annotations

import os
from typing import Any, Protocol

from app.contracts import AgentRequest


TOOLBOX_NAME_ENV_VAR = "BANKING_AGENT_TOOLBOX_NAME"
# A tool round is an extra model call plus tool execution, so it is bounded
# to keep the hosted-agent invocation inside its timeout budget.
MAX_TOOL_CALLS = 4


class ToolboxUnavailableError(RuntimeError):
    """Raised when a toolbox is configured but cannot be loaded.

    Configured-but-broken must fail loudly rather than silently returning no
    tools, which would look like a healthy agent giving a worse answer.
    """


class SupportsToolCall(Protocol):
    name: str

    async def ainvoke(self, args: dict[str, Any]) -> Any: ...


def toolbox_name() -> str | None:
    """The configured toolbox name, or None when the feature is disabled."""
    raw = os.getenv(TOOLBOX_NAME_ENV_VAR, "").strip()
    return raw or None


def toolbox_enabled() -> bool:
    return toolbox_name() is not None


async def load_tools() -> list[Any]:
    """Load toolbox tools as LangChain tools.

    Returns an empty list when the feature is disabled so callers can treat
    "no toolbox" and "toolbox with no tools" identically.
    """
    name = toolbox_name()
    if name is None:
        return []

    try:
        from langchain_azure_ai.tools import AzureAIProjectToolbox
    except ImportError as error:  # pragma: no cover - dependency guard
        raise ToolboxUnavailableError(
            f"{TOOLBOX_NAME_ENV_VAR} is set but langchain-azure-ai[hosting] is not "
            "installed in this image."
        ) from error

    toolbox = AzureAIProjectToolbox(toolbox_name=name)
    return await toolbox.get_tools()


def _tool_index(tools: list[Any]) -> dict[str, Any]:
    return {getattr(tool, "name", ""): tool for tool in tools}


def _requested_calls(response: Any) -> list[dict[str, Any]]:
    calls = getattr(response, "tool_calls", None)
    if not isinstance(calls, list):
        return []
    return [call for call in calls if isinstance(call, dict)]


async def gather_findings(
    model: Any,
    tools: list[Any],
    instructions: str,
    request: AgentRequest,
) -> list[str]:
    """Run one bounded round of toolbox tool calls and return observations.

    Observations are returned as plain strings so the caller can record them
    in ``evidence``. Every tool call a customer-facing answer depends on has
    to be visible in the audit trail, so a silent tool call is not acceptable.
    """
    if not tools:
        return []

    tool_model = model.bind_tools(tools)
    response = await tool_model.ainvoke(
        [
            ("system", instructions),
            (
                "user",
                f"Trace ID: {request.trace_id}\n"
                f"Customer request: {request.message}\n"
                f"Context: {request.specialist_context}",
            ),
        ]
    )

    index = _tool_index(tools)
    findings: list[str] = []
    for call in _requested_calls(response)[:MAX_TOOL_CALLS]:
        name = str(call.get("name", ""))
        tool = index.get(name)
        if tool is None:
            # The model hallucinated a tool that the toolbox does not expose.
            # Recording it keeps the audit trail honest about what was asked.
            findings.append(f"Tool '{name}' was requested but is not available in the toolbox.")
            continue

        try:
            observation = await tool.ainvoke(call.get("args") or {})
        except Exception as error:  # noqa: BLE001 - a tool failure must not fail the agent
            findings.append(f"Tool '{name}' failed: {error}")
            continue

        findings.append(f"Tool '{name}' returned: {observation}")

    return findings
