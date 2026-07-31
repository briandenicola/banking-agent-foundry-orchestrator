# Squad Decisions

## Active Decisions

### Architecture & Core
- **2026-07-29:** The project will use a C# orchestrator agent built with Microsoft Agent Framework and MCP to invoke Microsoft Foundry-hosted LangGraph agents as tools.
- **2026-07-29:** Authentication will use Microsoft Entra ID and managed identity; API keys are not allowed for service authentication.
- **2026-07-29:** Azure Container Apps, Terraform, and GitHub Actions are the default deployment path for the reference implementation.
- **2026-07-29:** Layered architecture enforced: Domain → Application → Infrastructure → API; all state transitions emit structured audit trails with correlation IDs.

### CI/CD & Deployment
- **2026-07-31:** CI/CD modernization (Issue #10) — OIDC-only authentication (no long-lived AZURE_CREDENTIALS); split workflows (ci.yml for PR/push validation, deploy-production.yml for main-only deployment); all 7 container images built in CI matrix; remote state with blob-lease locking and workspace-per-region environment separation; production approval gate via GitHub environment with required-reviewer rule.

### Runtime & Persistence
- **2026-07-30:** Domain exceptions (not EF exceptions) cross repository boundaries. Repositories catch `DbUpdateConcurrencyException` internally and re-throw domain type `StaleVersionException`. Application and API code never reference `Microsoft.EntityFrameworkCore`.
- **2026-07-30:** Multi-step persistence in StartAsync: Version incremented sequentially (Draft→0, after-planner→1, after-routing→2, terminal→3). Intermediate state persisted after each agent call; audit trail shows progress. OptimisticConcurrency via `IsConcurrencyToken` on Version.
- **2026-07-30:** Service-level idempotency check before RecordDecisionAsync. Same approval decision returns current state immediately; different decision throws ConflictingDecisionException. Repository-level check provides race-condition guard.
- **2026-07-30:** NpgsqlDataSource as singleton (owns connection pool and token refresh), DbContext as scoped (per-request). `UsePeriodicPasswordProvider` ensures Entra tokens refresh every 4 minutes independently of request lifecycle.

### Authentication & Authorization
- **2026-07-30:** Web UI → Orchestrator service-to-service auth uses app-role pattern (not delegated scope). Web UI managed identity holds `Workflow.Invoke` role on orchestrator API registration. Token scope: `api://{orchestrator_app_client_id}/.default`.
- **2026-07-30:** Orchestrator JWT bearer auth via `Microsoft.Identity.Web` (Phase B, after Theo's persistence work complete). Endpoint group requires authorization; `/health` remains anonymous.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction

### Testing & Quality (Issue #9)
- **2026-07-31:** Test work split across three owners to minimize merge conflicts (Issue #9 — comprehensive test coverage). Theo owns .NET tests (E2E lifecycle, restart recovery, idempotency, ProblemDetails); Lumen owns Python hosted-agent tests (timeout, error handling, boundary contracts); Nia owns CI/Taskfile/docs (E2E gate, test:e2e/test:hosted/test:all tasks, docs/testing.md). All E2E tests run in CI before deploy-production.yml. No test-only endpoints; no auth bypass; system SQLite only; Python tests from `src/agents/python` working directory.
- **2026-07-31:** Hosted Python agent (`app/hosted.py`) implements bounded timeout (30s, tunable via `AGENT_INVOKE_TIMEOUT_SECONDS`) and safe structured errors. Invalid JSON or request validation returns HTTP 400, timeout returns HTTP 504, and graph/result failures return a generic HTTP 500 without exposing exception details.
- **2026-07-31:** E2E tests in `BankingAgent.Api.Tests` exercise the HTTP endpoints, JWT policy, `WorkflowService`, real EF repositories, evidence persistence, and Web UI `IndexModel` against system SQLite. Deterministic MCP doubles replace live Foundry calls; no test-only production endpoints or authentication bypasses are used.
