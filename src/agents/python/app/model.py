from __future__ import annotations

import logging
import os
from collections.abc import Awaitable, Callable
from typing import TypeVar

from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from langchain_openai import AzureChatOpenAI, ChatOpenAI
from pydantic import BaseModel

from app.contracts import CONTRACT_VERSION, AgentName, AgentRequest, AgentResult


logger = logging.getLogger(__name__)

StructuredReasoner = Callable[[AgentName, str, AgentRequest], Awaitable[AgentResult]]
TStep = TypeVar("TStep", bound=BaseModel)
PROJECT_ENDPOINT_ENV_VAR = "BANKING_AGENT_PROJECT_ENDPOINT"
LEGACY_PROJECT_ENDPOINT_ENV_VAR = "FOUNDRY_PROJECT_ENDPOINT"


class ModelUnavailableError(RuntimeError):
    """Raised when no model endpoint is configured and deterministic
    fallback has been explicitly disabled (e.g. in production). Callers
    must surface this as an explicit failure, never a success-shaped
    fallback response.
    """


def _fallback_allowed() -> bool:
    """Whether deterministic local fallback may be used when no model
    endpoint is configured.

    Fallback is **strictly opt-in**: only an affirmative value for
    ``ALLOW_FALLBACK`` (``true``, ``1``, ``yes``, or ``on``, all
    case-insensitive and whitespace-tolerant) enables the local
    deterministic path.  Unset, empty, ``false``, ``0``, ``no``,
    ``off``, and any other value all disable fallback so that missing
    model configuration surfaces as an explicit failure rather than a
    silent success-shaped degradation.
    """
    raw = os.getenv("ALLOW_FALLBACK")
    if raw is None:
        return False
    return raw.strip().lower() in {"true", "1", "yes", "on"}


def project_endpoint() -> str | None:
    """The Foundry project endpoint this container was deployed with.

    Shared with the toolbox because Foundry reserves the ``FOUNDRY_*`` and
    ``AGENT_*`` prefixes, so this deployment cannot use the variable names the
    Azure SDKs discover on their own. Anything needing the endpoint has to be
    handed it explicitly from here.
    """
    raw = os.getenv(PROJECT_ENDPOINT_ENV_VAR) or os.getenv(LEGACY_PROJECT_ENDPOINT_ENV_VAR)
    return raw.strip() or None if raw else None


def _model() -> AzureChatOpenAI | ChatOpenAI | None:
    foundry_endpoint = project_endpoint()
    deployment = os.getenv("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-5.4-mini")
    credential = DefaultAzureCredential()

    if foundry_endpoint:
        token_provider = get_bearer_token_provider(
            credential,
            "https://ai.azure.com/.default",
        )
        return ChatOpenAI(
            base_url=f"{foundry_endpoint.rstrip('/')}/openai/v1/",
            model=deployment,
            api_key=token_provider,
        )

    endpoint = os.getenv("AZURE_OPENAI_ENDPOINT")
    if not endpoint:
        return None

    token_provider = get_bearer_token_provider(
        credential,
        "https://cognitiveservices.azure.com/.default",
    )
    return AzureChatOpenAI(
        azure_endpoint=endpoint,
        azure_deployment=deployment,
        api_version=os.getenv("AZURE_OPENAI_API_VERSION", "2025-04-01-preview"),
        azure_ad_token_provider=token_provider,
    )


async def structured_step(
    instructions: str,
    request: AgentRequest,
    schema: type[TStep],
    step_context: str | None = None,
) -> TStep | None:
    """Run one structured-output model call for a single graph node.

    Multi-node graphs need per-node output schemas rather than the terminal
    ``AgentResult``, and each node needs its own deterministic path when no
    model is configured. Returning ``None`` signals "no model available, apply
    your fallback" so that fallback logic stays with the node that owns the
    decision instead of being centralised in this module.

    Raises ``ModelUnavailableError`` when fallback is disabled, matching
    :func:`reason`, so a misconfigured production deployment fails loudly
    rather than silently degrading.
    """
    model = _model()
    if model is None:
        if not _fallback_allowed():
            raise ModelUnavailableError(
                "No model endpoint is configured and deterministic fallback "
                "is disabled (ALLOW_FALLBACK=false)."
            )
        return None

    structured_model = model.with_structured_output(schema)
    user_content = (
        f"Trace ID: {request.trace_id}\n"
        f"Customer request: {request.message}\n"
        f"Context: {request.specialist_context}"
    )
    if step_context:
        user_content += f"\n\nEarlier steps established:\n{step_context}"

    return await structured_model.ainvoke(
        [
            ("system", instructions),
            ("user", user_content),
        ]
    )


async def tool_findings(instructions: str, request: AgentRequest) -> list[str]:
    """Run one bounded toolbox round, or return nothing when unconfigured.

    Kept here so nodes depend on a single model-layer entry point rather than
    constructing a chat client themselves.

    A toolbox failure degrades this agent's answer; it does not destroy it.
    That boundary is the point of this try, and it is drawn to match what
    ``gather_findings`` already does for an individual tool that raises: the
    failure becomes an observation in the audit trail rather than an exception.
    Loading the toolbox and binding it to the model previously sat outside that
    protection, so a broken toolbox took down the whole agent invocation while a
    broken *tool* did not -- the same class of fault with opposite outcomes.

    Failing loudly is still the requirement, and it is still met: the failure is
    logged at error level and returned as evidence, so it reaches the workflow's
    audit trail and the operator. What it no longer does is deny the customer an
    explanation that the model was perfectly capable of producing without tools.
    """
    from app.toolbox import gather_findings, load_tools, toolbox_enabled

    if not toolbox_enabled():
        return []

    model = _model()
    if model is None:
        return []

    try:
        tools = await load_tools()
        return await gather_findings(model, tools, instructions, request)
    except Exception as error:  # noqa: BLE001 - see the docstring
        # The type is logged rather than the message because a toolbox error can
        # carry endpoint and token detail, and this text is persisted as
        # evidence and shown in the UI.
        logger.error(
            "Toolbox round failed with error type %s (trace_id=%s)",
            type(error).__name__,
            request.trace_id,
        )
        return [
            "The toolbox was unavailable for this request, so the answer was "
            "produced without tool assistance."
        ]


async def reason(agent: AgentName, instructions: str, request: AgentRequest) -> AgentResult:
    model = _model()
    if model is None:
        if not _fallback_allowed():
            raise ModelUnavailableError(
                "No model endpoint is configured and deterministic fallback "
                "is disabled (ALLOW_FALLBACK=false)."
            )
        return _local_result(agent, request)

    structured_model = model.with_structured_output(AgentResult)
    context = request.specialist_context
    result = await structured_model.ainvoke(
        [
            ("system", instructions),
            (
                "user",
                f"Trace ID: {request.trace_id}\n"
                f"Customer request: {request.message}\n"
                f"Context: {context}",
            ),
        ]
    )
    # Runtime-owned fields: model output must never be able to spoof agent
    # identity, status, trace id, contract version, or execution mode.
    return result.model_copy(
        update={
            "agent": agent,
            "status": "ok",
            "trace_id": request.trace_id,
            "contract_version": CONTRACT_VERSION,
            "execution_mode": "model",
        }
    )


def _local_result(agent: AgentName, request: AgentRequest) -> AgentResult:
    message = request.message.lower()

    if agent == AgentName.WORKFLOW_PLANNING:
        if any(term in message for term in ("dispute", "chargeback", "refund this charge")):
            selected_agent = AgentName.DISPUTE_PLANNING
            intent = "dispute"
            requires_approval = True
            risk_level = "high"
        elif any(term in message for term in ("fraud", "suspicious", "not my transaction", "not mine")):
            selected_agent = AgentName.SUSPICIOUS_ACTIVITY
            intent = "suspicious_activity"
            requires_approval = any(term in message for term in ("freeze", "block", "close"))
            risk_level = "high"
        else:
            selected_agent = AgentName.TRANSACTION_EXPLANATION
            intent = "transaction_explanation"
            requires_approval = False
            risk_level = "low"

        return AgentResult(
            agent=agent,
            trace_id=request.trace_id,
            contract_version=CONTRACT_VERSION,
            execution_mode="fallback",
            intent=intent,
            summary="Classified the request and selected a specialist agent.",
            risk_level=risk_level,
            requires_approval=requires_approval,
            recommended_action="Invoke the selected specialist agent.",
            next_step="invoke_specialist",
            selected_agent=selected_agent,
            evidence=["Local deterministic fallback was used because no model endpoint was configured."],
        )

    if agent == AgentName.TRANSACTION_EXPLANATION:
        return AgentResult(
            agent=agent,
            trace_id=request.trace_id,
            contract_version=CONTRACT_VERSION,
            execution_mode="fallback",
            intent="transaction_explanation",
            summary="The request asks for an informational explanation of a transaction.",
            risk_level="low",
            requires_approval=False,
            recommended_action="Explain the transaction status, merchant, timing, and likely causes.",
            next_step="respond_to_user",
            evidence=["No action-taking language was detected."],
        )

    if agent == AgentName.SUSPICIOUS_ACTIVITY:
        action_requested = any(term in message for term in ("freeze", "block", "close"))
        return AgentResult(
            agent=agent,
            trace_id=request.trace_id,
            contract_version=CONTRACT_VERSION,
            execution_mode="fallback",
            intent="suspicious_activity",
            summary="The request concerns potentially unauthorized account activity.",
            risk_level="high",
            requires_approval=action_requested,
            recommended_action="Summarize suspicious indicators and recommend protective next steps.",
            next_step="request_approval" if action_requested else "respond_to_user",
            evidence=["Suspicious-activity language was detected."],
        )

    return AgentResult(
        agent=agent,
        trace_id=request.trace_id,
        contract_version=CONTRACT_VERSION,
        execution_mode="fallback",
        intent="dispute",
        summary="The request asks to prepare or initiate a transaction dispute.",
        risk_level="high",
        requires_approval=True,
        recommended_action="Collect dispute details and prepare a bounded dispute action.",
        next_step="request_approval",
        evidence=["Dispute initiation is an action-taking workflow."],
    )
