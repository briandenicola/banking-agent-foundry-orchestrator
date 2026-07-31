# Squad Decisions

## Active Decisions

### Architecture & Core
- **2026-07-29:** The project will use a C# orchestrator agent built with Microsoft Agent Framework and MCP to invoke Microsoft Foundry-hosted LangGraph agents as tools.
- **2026-07-29:** Authentication will use Microsoft Entra ID and managed identity; API keys are not allowed for service authentication.
- **2026-07-29:** Azure Container Apps, Terraform, and GitHub Actions are the default deployment path for the reference implementation.
- **2026-07-29:** Layered architecture enforced: Domain → Application → Infrastructure → API; all state transitions emit structured audit trails with correlation IDs.

### Async Workflow Execution (Issue #9 Sprint)
- **2026-07-31:** Async workflow execution design approved (aria-async-workflow ADR). `POST /api/v1/workflows` returns 202 Accepted with Location header and Draft state (no inline planner/specialist). `WorkflowExecutionTrigger` atomically claims only the specific new Draft by ID; it cannot steal unrelated or active Recovering work. The periodic `WorkflowRecoveryWorker` guarantees stale-work pickup within `ScanIntervalSeconds` (30s prod, 5s dev). No schema migration required; Draft and Recovering states already exist.

- **2026-07-31:** Async workflow evidence ordering: For workflows expecting evidence (`ExpectsEvidence=true`), POST /workflows persists Draft without firing trigger; POST /evidence fires trigger after evidence persisted. For non-evidence workflows, trigger fires immediately after POST. This eliminates race conditions between specialist execution and evidence attachment.

- **2026-07-31:** Async workflow approval remains idempotent. `RecordDecisionAsync` checks if approval decision already exists; same decision returns current state, conflicting decision throws 409. `RecoverAsync` accepts Draft/Recovering (proceeds with routing) or terminal states (returns current state). UI implementation: exponential-backoff polling (1s → max 10s), 90s timeout with actionable notice, and stage tracking from durable workflow events. Accessibility: aria-live polite regions, aria-busy during submission, reduced-motion CSS flattens intervals to 5s. All accessible error messages non-empty.

- **2026-07-31:** Test work split for async workflow (Issue #9): Theo owns .NET E2E/recovery/idempotency tests; Lumen owns Python hosted-agent timeout/error tests; Nia owns CI/Taskfile/docs (E2E gate, test tasks, testing.md). Final count: 162 tests (Domain 19, Application 47, Infrastructure 16, Api 53, WebUi 27). All passing. No test-only production endpoints or auth bypasses. Python tests from `src/agents/python` working directory.

- **2026-07-31:** Hosted Python agent implements bounded timeout (30s, tunable via `AGENT_INVOKE_TIMEOUT_SECONDS`) with structured error handling: invalid JSON/validation → HTTP 400, timeout → HTTP 504, graph/result failures → generic HTTP 500 without exception details. E2E tests use deterministic MCP doubles (no live Foundry calls).

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

---

## Merged Decisions Archive

All inbox decisions have been reviewed for relevance and duplication:
- aria-async-workflow.md → Merged as async workflow execution decision
- aria-async-review.md → Final approval verdict (see test results below)
- nia-async-quality.md → Merged as async workflow testing strategy
- theo-async-implementation.md → Implementation details, files changed (Theo handoff)
- lumen-infra.md, lumen-service-auth.md, theo-observability.md, theo-runtime-persistence.md, nia-p0-validation.md, aria-p0-design-review.md → Pre-existing architecture decisions already captured in Active Decisions above

## Final Acceptance Criteria (Aria Review)

✅ All 8 criteria met:
1. POST returns 202 + Location; no inline planner/specialist
2. Execution authority atomic via workflow-specific `ClaimAsync` and stale-work `ClaimNextAsync`; trigger best-effort only
3. Evidence persisted before trigger fires; periodic worker guarantees pickup
4. Approval idempotent; no fabricated states
5. UI stages from server status+version; polling bounded (90s) and cancellable
6. Accessibility: aria-live, aria-busy, reduced-motion all present
7. Tests validate contract; smoke covers 202 polling semantics
8. Docs complete (functional-spec.md, technical-spec.md, testing.md)

✅ Build: `dotnet build -c Release --nologo` — 0 warnings, 0 errors
✅ Tests: `dotnet test -c Release --nologo` — 162/162 passing

---

## Issue #16 — Durable Progress Feedback (Source-Level Fixes)
- **2026-07-31:** Azure deployment runs pre-#7 (outdated) images; do not infer source behavior from deployed runtime. All source-level work remains local; no cloud remediation or deployment executed per parent directive.
- **2026-07-31:** Workflow UI stage indicators use durable event-type presence checks. Successful planner invocation persists `workflow.plan`; Investigate is active after planning until a terminal event; Decide is active for WaitingForApproval and done for other terminal states.
- **2026-07-31:** Issue #16 source-level work complete and reviewer-approved (Nia). Build and complete local quality gate pass. Deployment/smoke criterion parent-owned.
