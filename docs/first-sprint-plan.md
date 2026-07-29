# First Sprint Plan

## Sprint goal
Deliver the first end-to-end slice of the banking agent experience: a C# orchestrator API, a minimal UI, and a workflow path that accepts a request, creates a workflow state, and exposes approval state.

## Scope
- Create a versioned orchestrator API for workflow submission and approval.
- Implement the first workflow state model and approval lifecycle.
- Build a simple UI for submitting a request and viewing workflow status.
- Add an MCP integration abstraction that can be wired to a Foundry-backed tool in the next iteration.
- Keep all services aligned with Entra-only auth and structured tracing expectations.

## Sprint backlog

### 1. Orchestrator API foundation
- Add a versioned endpoint contract under `/api/v1/workflows`.
- Implement request and response DTOs.
- Return structured workflow data including trace ID, status, and message.

### 2. Workflow state and approval lifecycle
- Create domain types for workflow state and workflow events.
- Implement an in-memory workflow service with start and approval actions.
- Support a simple `Draft -> WaitingForApproval -> Approved/Rejected -> Completed` flow.

### 3. Minimal UI
- Add a lightweight web UI (Razor or simple static page served by the orchestrator) for:
  - entering a user request,
  - submitting it to the orchestrator,
  - viewing the resulting workflow trace ID and status,
  - triggering an approval action.

### 4. MCP abstraction and tool registry
- Add an `IMcpClient` abstraction and a stub implementation.
- Create a small registry concept so the orchestrator can later discover tools.
- Keep the interface generic enough for future Foundry-backed tool implementations.

### 5. Observability and validation
- Add structured logging for workflow start, approval, and errors.
- Add basic input validation and ProblemDetails-style error responses.
- Ensure secrets and PII are not logged.

## Definition of done
- The orchestrator can accept a request and return a workflow trace ID.
- A user can view workflow status in the UI.
- A user can submit an approval action through the UI or API.
- The repo builds locally and the CI workflow validates the initial build path.
