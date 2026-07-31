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

## Async workflow lifecycle

`POST /api/v1/workflows` returns **202 Accepted** immediately with a `Location` header pointing to the workflow status endpoint. The response body contains the `workflowId`, `traceId`, `status: "Draft"`, and a human-readable message. Background execution is claimed by the `WorkflowRecoveryWorker` or an immediate-trigger task fired after the Draft is persisted.

### Endpoint semantics

| Endpoint | Method | Behavior | Status code |
|---|---|---|---|
| `/api/v1/workflows` | POST | Persists Draft; enqueues execution; returns immediately | **202 Accepted** |
| `/api/v1/workflows/{id}` | GET | Returns current state, events, evidence, and support case | 200 OK |
| `/api/v1/workflows/{id}/approval` | POST | Idempotent approval; same decision is a no-op; different decision returns 409 | 200 / 409 |
| `/api/v1/workflows/{id}/evidence` | POST | Attaches evidence while workflow is not yet terminal | 200 / 400 |

### Workflow state lifecycle

```
POST → Draft (v0, persisted)
         ↓  (claimed atomically by recovery worker or immediate trigger)
       Recovering (v1)
         ↓
       WaitingForApproval | Completed | Failed
         ↓  (if approval required)
       Completed (after approve) | Rejected (after reject)
```

The immediate `ClaimAsync` path targets one workflow ID and accepts only `Draft`; the periodic `ClaimNextAsync` path accepts only stale `Draft` or `Recovering` rows. Both use atomic versioned updates, so exactly one replica wins the claim per workflow and active work cannot be stolen by an unrelated trigger.

### Polling behavior

The UI polls `GET /api/v1/workflows/{id}` with exponential backoff (1s → 2s → 4s → max 10s). Polling stops when the status is one of: `Completed`, `Failed`, `Rejected`, or `WaitingForApproval`. If the maximum poll duration (90s) is exceeded, the UI shows an actionable timeout with a Refresh button.

`RecoverAsync` returns the current state immediately (no error) for already-terminal workflows, making it safe to call from concurrent replicas.

### Evidence association

Evidence uploaded between `POST` (Draft creation) and specialist execution is durably linked to the workflow and visible to the specialist agent when it processes the claim. Evidence uploaded after `Completed` or `Failed` is rejected.

### Approval semantics

- Re-submitting the same decision for an already-decided workflow returns the current state (idempotent).
- Submitting a conflicting decision returns 409 `ConflictingDecisionException`.
- `RecordDecisionAsync` uses `expectedVersion` for optimistic concurrency — no duplicate execution.

### Migration/deployment notes

- **No schema migration required.** Draft/Recovering states and the claim pattern already exist.
- The `POST` change from 200 to 202 is a breaking change for callers expecting synchronous completion; the Web UI and smoke tests are updated simultaneously.
- The recovery worker `ScanIntervalSeconds` can be reduced to 5 for faster pickup in dev (default: 30 in production).
