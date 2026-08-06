# Technical Specification

## Solution shape
The reference implementation is now a C# workflow orchestrator that uses an Agent Framework-backed workflow path to call Microsoft Foundry-hosted LangGraph agents. All four hosted agents speak genuine MCP JSON-RPC 2.0 over the authenticated Foundry hosted-agent HTTP endpoint: the orchestrator performs `initialize`, discovers with `tools/list`, and invokes with `tools/call`. The versioned typed HTTP envelope remains only as a fallback for tools absent from `FOUNDRY_MCP_TOOL_ENDPOINTS`. The orchestration layer remains responsible for durable workflow state, approvals, correlation IDs, and API behavior, while the hosted agents remain specialized reasoning services. The hosted agents call Microsoft Foundry models directly; LiteLLM was removed in [ADR 0001](decisions/0001-remove-litellm-gateway.md). The implementation follows a layered structure: Domain → Application → Infrastructure → API/Web.

## Components
- C# orchestrator agent
  - Owns workflow state, approvals, correlation IDs, and API integration.
  - Exposes a versioned HTTP API for user requests and workflow updates.
  - Uses constructor injection and thin API handlers.
- MCP and typed agent integration
  - Connects the orchestrator to Microsoft Foundry-hosted LangGraph agents as specialized tools.
  - Uses real MCP for every agent tool; the typed envelope is a fallback for tools without an MCP endpoint.
  - Fails readiness when an MCP-enabled required tool is absent or its input schema is incompatible.
- Foundry-hosted LangGraph agents
  - Provide reasoning, planning, and specialized action capabilities as remote workflow services.
  - Call Microsoft Foundry models directly. There is no AI gateway; see [ADR 0001](decisions/0001-remove-litellm-gateway.md).
- Azure Container Apps
  - Hosts the orchestrator and any supporting gateway services as independently deployable services.
- Azure Database for PostgreSQL Flexible Server
  - Stores workflow state, events, evidence, approvals, actions, support cases, and demo transactions.
  - Uses EF Core/Npgsql, Entra authentication, optimistic version checks, and uniqueness constraints for idempotency.
- Azure Monitor / Application Insights / OpenTelemetry
  - Collects logs, traces, and operational telemetry for each workflow run.

## Runtime flow

### Async workflow execution (202 contract)
1. The user submits a request to the C# orchestrator via `POST /api/v1/workflows`.
2. The orchestrator persists a `Draft` workflow row (version 0) and returns **202 Accepted** immediately with a `Location` header and a `{ workflowId, traceId, status: "Draft", message }` body. The response is returned before any agent invocation.
3. An immediate-trigger `Task.Run` atomically claims the specific new `Draft` by ID and fires `RecoverAsync` as best-effort pickup. It cannot claim another workflow or reclaim active `Recovering` work.
4. The `WorkflowRecoveryWorker` provides guaranteed delivery within `ScanIntervalSeconds` (default 30s) by using `ClaimNextAsync` only for stale `Draft` or `Recovering` rows. Both claim paths use versioned conditional updates, so exactly one replica wins and transitions the workflow to `Recovering`.
5. The orchestrator invokes the planner and every specialist through MCP JSON-RPC, since `FOUNDRY_MCP_TOOL_ENDPOINTS` carries an endpoint for all four tools. A tool omitted from that map falls back to the typed envelope.
6. Routing authority is hybrid: a valid planner `selected_agent` chooses the specialist, while `WorkflowRoutingPolicy` validates as a guardrail that can only escalate `requires_approval` and never swaps the selected agent or de-escalates approval.
7. Planner/policy disagreements persist `workflow.route_disagreement` events with both agents, both approval decisions, and the winner. Missing or unrecognized planner routes fall back to the policy and persist `workflow.route_fallback` events with the reason and winning route.
8. If sensitive, the orchestrator sets `WaitingForApproval` and persists. The UI polls until this state is observed, then shows the approval form.
9. After approval, the orchestrator executes the bounded action, creates a support case (if applicable), and persists the audit trail.
10. All model calls are made by the hosted agents directly against Microsoft Foundry. There is no gateway in the request path.
11. The UI polls `GET /api/v1/workflows/{id}` with exponential backoff until a terminal status (`Completed`, `Failed`, `Rejected`, `WaitingForApproval`) is observed.

### Recovery and failure behavior
- `RecoverAsync` returns the current state without error if the workflow is already terminal (idempotent for concurrent replicas).
- Agent invocation failures (planner or specialist) persist a `Failed` workflow with a `workflow.failed` event before propagating or returning.
- Cancellation during execution also persists the `Failed` state.

## API and domain contract
- Public endpoints should be versioned and exposed under `/api/v1/...`.
- Controllers should be thin; business logic should live in application services and domain types.
- Request and response DTOs should be explicit and use immutable records where practical.
- Failed requests should return RFC 7807 ProblemDetails rather than raw exception text.
- `POST /api/v1/workflows` returns **202 Accepted** (not 200) — this is a breaking change from the synchronous prototype. Callers must handle the async lifecycle: read the `Location` header, then poll `GET` until a terminal status.
- `GET /api/v1/workflows/{id}` returns the full workflow state including events, evidence, and support case. This endpoint is safe to poll with exponential backoff.
- Terminal polling statuses: `Completed`, `Failed`, `Rejected`, `WaitingForApproval`. Non-terminal statuses (`Draft`, `Recovering`) indicate in-progress execution.
- Approval (`POST /api/v1/workflows/{id}/approval`) is idempotent for the same decision and returns 409 for conflicting decisions or stale versions.

## Security and governance
- Use managed identity for Azure resource access where possible.
- Never use keys for authentication; use Microsoft Entra ID for service-to-service authentication.
- Keep secrets in Azure Key Vault or environment-based secret stores only when unavoidable.
- Enforce approval gates for all sensitive actions.
- Store complete trace metadata for each workflow step.
- Include GitHub Actions workflows for build validation and deployment automation.
- Containers should run as non-root and should avoid embedding secrets in build or runtime configuration.

## Observability and quality
- Emit structured logs with correlation IDs and a request trace ID on every workflow event.
- Capture OpenTelemetry spans for agent calls, workflow transitions, approvals, and persistence operations.
- Validate inputs at the API boundary and avoid logging PII or sensitive data.
- Keep the quality gate local and repeatable: build, tests, formatting, and targeted validation for changed services.

## Infrastructure plan
- Terraform provisions the Azure Container Apps environment, Container Apps, managed identities, PostgreSQL Flexible Server, and supporting networking.
- Terraform should keep the deployment reproducible and environment-agnostic.
- The deployment should support separate environments for dev, test, and prod.
- CI/CD should use GitHub Actions and should deploy only after successful build validation.

## Proposed repo layout
- `/src/domain/` - domain models and policy rules
- `/src/application/` - workflow orchestration, use cases, and service contracts
- `/src/infrastructure/` - Azure, persistence, and external integration implementations
- `/src/api/` - versioned HTTP endpoints and DTOs
- `/src/agents/python/` - Python LangGraph/LangChain agents
- `/infrastructure/` - Terraform configuration (convention-over-configuration; `region` is the only input)
- `/docs/` - specifications and architecture notes
