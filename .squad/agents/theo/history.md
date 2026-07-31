# Theo History

Project: langgraph-learnings
Stack: C#, .NET 8, ASP.NET Core, Azure Container Apps, Terraform, GitHub Actions, MCP, Microsoft Foundry
Owner: brian
Description: A banking agent prototype where a C# orchestrator uses Microsoft Agent Framework and MCP to invoke Foundry-hosted LangGraph agents.

## Learnings
- Added Azure Monitor OpenTelemetry wiring to the orchestrator with a startup guard so local development stays clean when APPLICATIONINSIGHTS_CONNECTION_STRING is unset.
- Tagged the active request Activity with `correlation_id` in CorrelationIdMiddleware so traces and structured logs share the same request correlation value.

## Workstream 1 — Durable Runtime Persistence (2026-07-30)

Replaced `ConcurrentDictionary` in `WorkflowService` with full EF-backed PostgreSQL persistence. Implementation details:

### New files
- `src/domain/WorkflowExceptions.cs` — typed domain exceptions: `WorkflowNotFoundException`, `WorkflowConflictException`, `StaleVersionException`, `ConflictingDecisionException`, `InvalidTransitionException`.
- `src/infrastructure/Persistence/EfWorkflowRepository.cs` — implements `IWorkflowRepository` with explicit entity↔domain mapping (no AutoMapper), event-append by sequence count, EF optimistic concurrency via `IsConcurrencyToken`.
- `src/infrastructure/Persistence/EfWorkflowActionRepository.cs` — implements `IWorkflowActionRepository` with idempotency/conflict check for `RecordDecisionAsync`, support case mapping.

### Modified files
- `src/application/WorkflowService.cs` — constructor injects `IWorkflowRepository` + `IWorkflowActionRepository`; `StartAsync` now persists Draft state before MCP calls then updates after each phase (planner, route, specialist/terminal); `ApproveAsync` does service-level idempotency + `RecordDecisionAsync`; added `GetAsync` + `GetSupportCaseAsync` to `IWorkflowService` interface.
- `src/application/WorkflowRequest.cs` — added `WorkflowDetailResponse`, `WorkflowEventResponse`, `SupportCaseResponse` DTOs with a static `From(WorkflowState, SupportCase?)` factory.
- `src/api/WorkflowEndpoints.cs` — added `GET /api/v1/workflows/{workflowId:guid}` endpoint; approval endpoint now returns 404 on `WorkflowNotFoundException` and 409 on `WorkflowConflictException`.
- `src/orchestrator/Program.cs` — registers `NpgsqlDataSource` singleton with `UsePeriodicPasswordProvider` + `ManagedIdentityCredential` (guard: only when `AZURE_CLIENT_ID` is set); `AddDbContext<BankingAgentDbContext>` scoped; `EfWorkflowRepository` + `EfWorkflowActionRepository` scoped; `IWorkflowService` changed from Singleton → Scoped. No `MigrateAsync` on startup.
- `tests/BankingAgent.Application.Tests/WorkflowServiceCurrentBehaviorTests.cs` — updated constructor to supply mock repos; updated exception expectations to domain types; idempotent double-approve now passes (correct per design).
- `tests/BankingAgent.Application.Tests/WorkflowRepositoryContractTests.cs` — removed stale `Microsoft.EntityFrameworkCore` using; updated version-conflict test to throw/assert `StaleVersionException`.
- `tests/BankingAgent.Api.Tests/BankingAgent.Api.Tests.csproj` — added `Microsoft.AspNetCore.Authentication.JwtBearer 10.0.0` package (net10.0 ref pack not installed locally).
- `tests/BankingAgent.Api.Tests/TestOrchestratorHost.cs` — added `services.AddRouting()`; wired GET route to `IWorkflowService.GetAsync` (was 501 stub).
- `tests/BankingAgent.Api.Tests/WorkflowEndpointContractTests.cs` — removed EF using; `DbUpdateConcurrencyException` → `StaleVersionException`; GET test updated from 501 → 404.

### Patterns learned
- `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider` is the correct pattern for Entra-managed DB token refresh (matches database-migrator pattern).
- EF change tracker within a scoped DbContext naturally handles multi-step `AddAsync`/`UpdateAsync` sequences for a single workflow — entities loaded by ID return the tracked instance, so event counts are consistent.
- Transitive NuGet packages are available in .NET 10 SDK builds, so `Azure.Identity` and `Npgsql` types are usable in orchestrator without adding direct references.
- `IsConcurrencyToken()` on `Version` + catching `DbUpdateConcurrencyException` → `StaleVersionException` keeps EF out of Domain/Application.
- net10.0 test projects need `Microsoft.AspNetCore.Authentication.JwtBearer` as an explicit NuGet package since `Microsoft.AspNetCore.App.Ref` 10.0 packs are not installed in this environment.

### Validation
- `dotnet build src/orchestrator/orchestrator.csproj -c Release` — 0 errors, 0 warnings
- `dotnet test BankingAgent.Application.Tests` — 15/15 passed
- `dotnet test BankingAgent.Api.Tests` — 19/19 passed

## Workstream — CI/Deploy Revision (Issue #10, 2026-07-31)

Revision pass after Aria's blocking review findings. Four gaps corrected:

### Fix 1 — Environment secret scope (deploy-production.yml)
`build-and-push` referenced `secrets.TF_BACKEND_*` (environment-scoped) without `environment: production`.
Added `environment: production` to the job. All four jobs (`build-and-push`, `deploy-infrastructure`, `deploy-apps`, `smoke`) now run inside the environment gate, so the required-reviewer approval gate fires before any job starts. Updated the OIDC comment to remove the inaccurate `ref:refs/heads/main` federated-credential note.

### Fix 2 — Infrastructure test suite in CI (ci.yml)
`BankingAgent.Infrastructure.Tests` was absent from the CI dotnet job. Added a `Unit tests – Infrastructure` step alongside the other suites. Also added the suite to `task test:unit` in `Taskfile.test.yml`.

### Fix 3 — Python pytest suites in CI (ci.yml)
Two Python test files existed but were never run:
- `src/agents/python/tests/test_agents.py` (agent graph unit tests)
- `src/agents/deployer/test_deploy.py` (deployer contract tests)
Added `python-agent-tests` and `python-deployer-tests` jobs. Each installs only its own `requirements.txt` then runs pytest. Added `python-agents` and `python-deployer` tasks to `Taskfile.test.yml`.

### Fix 4 — Deployment concurrency (deploy-production.yml)
Added a `concurrency` block at workflow level: group `deploy-production`, `cancel-in-progress: false`. Prevents overlapping production deploys without killing a deploy that is mid-flight.

### Validation
- YAML lint on ci.yml and deploy-production.yml — PASS
- Structural checks (all jobs, all environments, concurrency block) — PASS
- `git status` confirms changes are unstaged as required

---

## Cross-Agent Feedback from Issue #10 Revision (2026-07-31)

**From Aria (final review):** All blocking issues from the first review cycle have been resolved. Implementation satisfies every acceptance criterion. The revision demonstrates surgical, focused fixes without scope creep. Ready for commit and merge with one non-blocking advisory about TF_VAR_image_tag placeholder.

---

## Workstream — Issue #9 Test Expansion (.NET, 2026-07-31)

Added four new test areas per the aria-issue-9-design-review.md split decision.

### New files
- `tests/BankingAgent.Api.Tests/WorkflowE2eTests.cs` — E2E lifecycle tests with real `WorkflowService`, deterministic MCP client, and in-memory repository implementations. Three scenarios: dispute approve → support case visible in GET; explanation completes without support case; support-case ID stable across repeated GET calls. `[Trait("Category", "E2E")]` applied. SQLite-backed persistence correctness lives in Infrastructure.Tests; Api.Tests E2E focuses on the HTTP → endpoint → service chain.

### Modified files
- `tests/BankingAgent.Api.Tests/TestOrchestratorHost.cs` — added second constructor overload accepting `IWorkflowService`/`IWorkflowEvidenceService` directly (instead of mocks); `ConfigureServices` refactored to accept interfaces; existing mock-accepting constructor delegates to new overload.
- `tests/BankingAgent.Api.Tests/WorkflowEndpointContractTests.cs` — added `PostApproval_SameDecisionIdempotent_ResponseBodyIsIdenticalBetweenCalls`: asserts that two calls with the same decision return identical `workflowId` + `status` JSON fields, not just 200 OK.
- `tests/BankingAgent.Api.Tests/ProblemDetailsContractTests.cs` — added `PostEvidence_OversizedFile_ReturnsProblemDetailsWithEvidenceInvalidCode`: sends a file 1 byte over `WorkflowEvidenceService.MaximumFileBytes` (10 MB) and asserts 400 BadRequest + `evidence_invalid` ProblemDetails code.
- `tests/BankingAgent.Infrastructure.Tests/WorkflowRestartRecoveryTests.cs` — added `ApprovalRetry_AfterContextRestart_ProducesNoDuplicateActionsOrCases`: starts a real dispute workflow via `StartAsync` + `DisputeDeterministicMcpClient`, approves it, tears down the context (simulating a process crash before response delivery), then retries `ApproveAsync` from a new context and proves exactly 1 ApprovalDecision, 1 ActionExecution, 1 SupportCase row.

### Patterns learned
- `WorkflowService` is stateless enough to use as a singleton in test hosts when backed by a fresh EF context or in-memory repository per test — each service method reads fresh state from the repository.
- The `TestOrchestratorHost` constructor delegation pattern (mock overload → real-services overload) keeps all service configuration in one place without breaking existing contract tests.
- `NuGet restore` is permission-gated in this environment for packages not already cached (e.g. `sqlitepclraw.core 2.1.6`). Adding new packages to Api.Tests csproj without a successful `dotnet restore` is not possible; in-memory repository implementations are the correct fallback for Api.Tests E2E.
- `DisputeDeterministicMcpClient` must return `agent = "dispute-planning"` from the `dispute.plan` tool call to pass `TryReadAgentResult`'s agent-name assertion in `WorkflowService`.

### Validation
- `dotnet test tests/BankingAgent.Api.Tests/` — 34/34 passed (was 29)
- `dotnet test tests/BankingAgent.Infrastructure.Tests/` — 14/14 passed (was 13)
- `dotnet test tests/BankingAgent.Application.Tests/` — 43/43 passed (unchanged)

## Workstream — Async Workflow UI Implementation (2026-07-31)

Implemented async POST/poll semantics per aria-async-workflow.md decision.

### Key Changes

**Backend:**
- `WorkflowService.StartAsync/StartDemoAsync` — now persist Draft and return immediately (no routing). Routing lives in `RecoverAsync`, called by `WorkflowRecoveryWorker` or the new immediate trigger.
- `WorkflowService.RecoverAsync` — accepts Draft + Recovering for execution; returns current state for terminal/approval states (idempotent).
- `IWorkflowExecutionTrigger` + `WorkflowExecutionTrigger` — safe singleton pattern using `IServiceScopeFactory`. Fires after POST (non-dispute) or after evidence upload (dispute), ensuring evidence is persisted before specialist runs.
- `WorkflowEndpoints.cs` — returns 202 Accepted + Location header. `IWorkflowExecutionTrigger?` nullable for test compatibility.
- `WorkflowRecoveryOptions.StaleAfterSeconds` — default 120→10, min 30→5 for faster dev pickup.

**WebUI:**
- `Index.cshtml.cs` — `OnGetPollAsync` proxies GET /api/v1/workflows/{id} to orchestrator, returns JSON.
- `Index.cshtml` — compact above-fold layout, stage track (Plan/Investigate/Decide), `aria-live` region, `aria-busy`/disabled forms, failure/timeout cards.
- `site.js` — exponential back-off polling (1→2→4→8→10s cap, 90s max), terminal/approval stop conditions, stage/timeline updates, `AbortSignal.timeout` per-poll, reduced-motion support.
- `site.css` — stage-dot animations, spinner, timeout notice, failure card, compact `has-workflow` layout, reduced-motion and forced-colors media queries.

### Test Impact
- 52/53 API tests pass (1 failure: Nia's E2E test using real StartAsync expecting planner failure — tests OLD behavior)
- 34/47 Application tests pass (13 failures: all test synchronous routing via StartAsync — OLD behavior, need Nia to update)
- 15/16 Infrastructure tests pass (1 failure: Nia's new test bug — workflow 1min old vs staleBefore 2min ago)

### Patterns Learned
- `Results.Accepted(uri, body)` sets 202 status with Location header in minimal APIs.
- `IWorkflowExecutionTrigger?` as nullable in minimal API endpoint parameters: ASP.NET Core injects null without throwing if service not registered, when `[FromServices]` attribute is present.
- `WorkflowRecoveryWorker` uses `IServiceScopeFactory` (singleton-safe); same pattern can be used in `WorkflowExecutionTrigger` without violating DI lifetime rules.
- Aria-live `aria-atomic="true"` with empty+replace content pattern ensures consistent screen reader announcements across assistive technologies.
- `AbortSignal.timeout(ms)` (per-request timeout) + exponential back-off polling avoids indefinite waits and handles connection failures gracefully.

## Workstream — Issue #16 Stage Model Alignment (2026-07-31)

Reviewed the full implementation against Issue #16 acceptance criteria. Identified two real source-level gaps.

### Gap 1 — `updateStages` version proxy (site.js)
The JS `updateStages` function used `version` (an integer) to infer which stage was active. The comment itself called it a "rough proxy." When a workflow fails during the planner phase, version increments to 1 but the Plan stage should not show as done. Using audit event types from `data.events` (which are already returned by GET) is truthful and robust.

**Fix:** Replaced version logic with event-type presence checks:
- `planDone` = `events.some(e => e.type === "workflow.plan")`
- `terminalDone` = any of `workflow.completed | workflow.approval_required | workflow.failed`

### Gap 2 — SSR stage classes wrong for WaitingForApproval (Index.cshtml)
The Razor server-side stage classes used `isTerminal` which excludes WaitingForApproval. Result: on a direct page load of a WaitingForApproval workflow, Plan showed as `stage-active` (wrong) and Investigate had no class (wrong). Since `data-polling="false"` for WaitingForApproval, the polling loop never ran to correct it.

**Fix:** Added `hasPlanEvent` and `hasTerminalEvent` Razor variables derived from `Model.Workflow.Events` (same event types as the JS fix), and used them for the SSR stage class ternaries.

### Test coverage
- Added `OnGetAsync_WaitingForApproval_RealisticEvents_StageAuditTypesPresent` to `DemoScenarioUiTests.cs`
- Added `BuildWorkflowDetailJsonWithEventTypes` helper for typed-event test data
- WebUI: 28 tests (was 27), all passing
- Full aggregate: 190 tests (was 189), all passing

### Patterns learned
- Stage state should derive from durable server events, not version counters, for correctness on failure paths and direct page loads
- SSR and JS stage logic must use the same event-type contract so the initial render and subsequent polling produce consistent results
- WebUI Razor model tests don't render HTML — stage class correctness is validated via event-type exposure tests + correctness of the Razor ternary logic

## Workstream 4 — Optional EvidenceFiles Fix (2026-07-31)

Fixed root cause of frozen-UI-on-no-evidence bug (aria-optional-evidence-gate approved contract).

### Files changed
- `src/webui/Pages/Index.cshtml.cs` — `InputModel.EvidenceFiles` changed to `List<IFormFile>?`; coalesced once at server boundary (`var files = Input.EvidenceFiles ?? []`); all downstream references updated to `files`.
- `src/webui/wwwroot/js/site.js` — submit handler now checks `jqForm?.valid?.() ?? form.checkValidity()` before entering loading state; `e.preventDefault()` on invalid; controls left untouched on invalid path.

### Root cause
Non-nullable `List<IFormFile>` caused Razor tag-helpers + jQuery unobtrusive validation to emit `data-val-required`, canceling the submit event after `site.js` had already set `is-loading` and disabled controls — leaving the form frozen with zero network traffic.

### Validation
- Build: `dotnet build src/webui/webui.csproj -c Release --nologo` — 0 warnings, 0 errors ✅
- Tests: `dotnet test tests/BankingAgent.WebUi.Tests/ -c Release --nologo` — 28/28 passing ✅
- Squad inbox: `.squad/decisions/inbox/theo-optional-evidence-fix.md` written
