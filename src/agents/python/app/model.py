from __future__ import annotations

import os
from collections.abc import Awaitable, Callable

from azure.identity import DefaultAzureCredential, get_bearer_token_provider
from langchain_openai import AzureChatOpenAI

from app.contracts import AgentName, AgentRequest, AgentResult


StructuredReasoner = Callable[[AgentName, str, AgentRequest], Awaitable[AgentResult]]


def _model() -> AzureChatOpenAI | None:
    endpoint = os.getenv("AZURE_OPENAI_ENDPOINT") or _openai_endpoint_from_project()
    deployment = os.getenv("AZURE_AI_MODEL_DEPLOYMENT_NAME", "gpt-5.4-mini")
    if not endpoint:
        return None

    token_provider = get_bearer_token_provider(
        DefaultAzureCredential(),
        "https://cognitiveservices.azure.com/.default",
    )
    return AzureChatOpenAI(
        azure_endpoint=endpoint,
        azure_deployment=deployment,
        api_version=os.getenv("AZURE_OPENAI_API_VERSION", "2025-04-01-preview"),
        azure_ad_token_provider=token_provider,
    )


def _openai_endpoint_from_project() -> str | None:
    project_endpoint = os.getenv("FOUNDRY_PROJECT_ENDPOINT")
    if not project_endpoint:
        return None

    host = project_endpoint.split("/api/projects/", 1)[0]
    return host.replace(".services.ai.azure.com", ".openai.azure.com")


async def reason(agent: AgentName, instructions: str, request: AgentRequest) -> AgentResult:
    model = _model()
    if model is None:
        return _local_result(agent, request)

    structured_model = model.with_structured_output(AgentResult)
    result = await structured_model.ainvoke(
        [
            ("system", instructions),
            (
                "user",
                f"Trace ID: {request.trace_id}\n"
                f"Customer request: {request.message}\n"
                f"Context: {request.context}",
            ),
        ]
    )
    return result.model_copy(update={"agent": agent, "trace_id": request.trace_id})


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
        intent="dispute",
        summary="The request asks to prepare or initiate a transaction dispute.",
        risk_level="high",
        requires_approval=True,
        recommended_action="Collect dispute details and prepare a bounded dispute action.",
        next_step="request_approval",
        evidence=["Dispute initiation is an action-taking workflow."],
    )
