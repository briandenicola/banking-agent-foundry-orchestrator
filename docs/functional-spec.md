# Functional Specification

## Product goal
Build a banking support agent prototype that can assist with transaction questions and safely carry out approved actions through a multi-step workflow orchestrated by a C# Agent Framework agent.

## Primary user journey
1. A user submits a request such as "Explain this pending transaction" or "Dispute this charge".
2. The C# orchestrator agent classifies the request and decides which specialized tool to call.
3. The orchestrator loads Microsoft Foundry-hosted LangGraph agents as MCP tools and invokes them for reasoning or planning.
4. If sensitive, the orchestrator pauses and requires explicit approval.
5. After approval, the orchestrator executes the action and records a complete audit trail.

## Core features
- A C# orchestrator agent built with Microsoft Agent Framework.
- MCP-based loading of Microsoft Foundry-hosted LangGraph agents as tools.
- A minimal web UI for entering requests, viewing workflow status, and approving sensitive actions.
- Approval-required workflow for sensitive actions.
- Structured logs and traces for every workflow step.
- Model access through a LiteLLM gateway for routing and provider abstraction where direct model access is needed.
- Support for informational responses and bounded actions.
- Versioned REST APIs and structured error responses.

## Example scenarios
- Explain a recent purchase and provide likely reasons.
- Summarize suspicious transactions and recommend next steps.
- Create a support case after a dispute request.
- Handle a dispute initiation only after approval and validation.

## Functional requirements
- The system must accept a user message and return a workflow response through a versioned API contract, such as `/api/v1/workflows`.
- The system must provide a minimal web UI that allows users to submit requests, monitor workflow status, and complete approvals.
- The orchestrator must be able to discover and invoke MCP tools representing Microsoft Foundry-hosted LangGraph agents.
- The workflow must distinguish between read-only and action-taking intents.
- Sensitive actions must require explicit approval before execution.
- The system must return a trace identifier that links the request, agent decisions, approval steps, and outcome.
- The system must store workflow metadata and audit events for later review.
- Protected API endpoints must require Microsoft Entra ID authentication and reject requests that do not satisfy the policy.
- The system must return standardized ProblemDetails responses for invalid input, policy rejection, and execution failures.
- The system must emit structured diagnostics for every request and agent step without logging secrets or PII.
- The deployment pipeline must support build validation and deployment through GitHub Actions.

## Out of scope for v1
- Live bank integrations or real transaction settlement.
- Advanced policy engine beyond simple approval rules.
- Multi-tenant or highly regulated compliance controls.
- Any authentication mechanism that uses API keys or shared secrets for service-to-service access.
