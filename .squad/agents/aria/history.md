# Aria History

Project: langgraph-learnings
Stack: C#, .NET 8, Azure Container Apps, Terraform, GitHub Actions, MCP, Microsoft Foundry
Owner: brian
Description: A banking agent prototype where a C# orchestrator uses Microsoft Agent Framework and MCP to invoke Foundry-hosted LangGraph agents.

## Phase 1 Kickoff (2026-07-29)

### Key Decisions
1. **MCP abstraction first:** `IMcpClient` interface enables testing and swappability. Hardcoded tool registry in Phase 1; Foundry dynamic discovery deferred.
2. **Layered architecture enforcement:** Domain models (WorkflowState, ApprovalGate, AuditEvent) live in domain layer; orchestration logic in application layer; MCP integration in infrastructure layer.
3. **No secrets in Terraform:** Managed identity only; role assignments via Terraform; Key Vault integration deferred.
4. **Environment-agnostic tfvars pattern:** Separate dev.tfvars, test.tfvars, prod.tfvars for promotion safety.
5. **CI/CD staged validation:** Phase 1 validates builds and formatting only; container pushes and Terraform apply deferred to Phase 2.

### Critical Patterns
- **Correlation ID on every workflow step:** TraceId UUID generated on POST /api/v1/workflow; threaded through all MCP calls and audit events.
- **Audit events as first-class domain objects:** Every state transition (received → processing → approved → executed/failed) emits structured AuditEvent with timestamp, action, result, actor.
- **Thin controllers, thick domain:** API handlers delegate to `WorkflowOrchestrationService` immediately; business logic owns state machine and guardrails.
- **Error contracts:** Always return RFC 7807 ProblemDetails; never expose raw exception stacks to callers.

### File Locations to Reference
- Constitution: `docs/project-constitution.md` (source of truth for guardrails)
- Technical spec: `docs/technical-spec.md` (component contracts and runtime flow)
- Terraform base: `src/infra/terraform/main.tf` (placeholder resource pattern)
- Orchestrator stub: `src/orchestrator/Program.cs` (Minimal Apis base; replace with structured service injection in Phase 1)
- CI/CD base: `.github/workflows/build-and-deploy.yml` (currently placeholder; add validation steps in Phase 1)

### Learnings
- Constitution emphasizes "no keys ever" harder than typical projects; this shapes managed identity design from day 1.
- Approval gates before sensitive actions are non-negotiable; this affects workflow state machine and API contracts early.
- Audit trails must be structured from the start; unstructured logs won't meet banking compliance expectations later.
- Foundry agent integration is deferred strategically; Phase 1 proves the orchestrator and MCP contract with stubs.

### Future Attention
- LiteLLM routing (Phase 2): When does orchestrator call LiteLLM vs. Foundry agents directly? Document decision in ADR.
- HorizonDB schema (Phase 2): Exact storage of WorkflowState and AuditEvent; pagination and query patterns.
- Multi-agent coordination (Phase 2+): How do multiple MCP tools orchestrate? Dependency ordering? Branching workflows?

## P0 Design Review — Durable Persistence & Entra Service Auth (2026-07-30)

### Learnings
- The existing `IWorkflowRepository` and `IWorkflowActionRepository` interfaces are already well-designed for EF implementation — no interface changes needed. The `expectedVersion` parameter aligns perfectly with EF's `IsConcurrencyToken()` on `Version`.
- `WorkflowService` uses `ConcurrentDictionary` as a singleton; switching to scoped EF requires changing the DI lifetime from `AddSingleton` to `AddScoped`. This is a subtle but critical change — the FoundryMcpClient is registered via `AddHttpClient` (transient) so it stays compatible.
- The database-migrator already establishes the Entra token pattern for PostgreSQL (`ManagedIdentityCredential` + `https://ossrdbms-aad.database.windows.net/.default`). The orchestrator should use the same pattern but via `NpgsqlDataSourceBuilder.UsePeriodicPasswordProvider` for automatic token refresh.
- Service-to-service auth between webui and orchestrator requires app roles (not delegated scopes) because managed identities use client credentials flow. The `roles` claim in the JWT carries the app role, not `scp`.
- The `apps/providers.tf` does not currently include `azuread` provider or `data.azurerm_client_config.current` — both must be added for Entra app registration Terraform.
- `smoke-mvp.py` already has `ORCHESTRATOR_TOKEN_SCOPE` support and authentication baseline checking, so minimal smoke test changes are needed. The main gap is adding `ORCHESTRATOR_TOKEN_SCOPE` as a Terraform output.
- Both Theo and Lumen touch `src/orchestrator/Program.cs` — Theo for persistence composition, Lumen for auth middleware. The insertion points are structurally separate (service registration vs. middleware pipeline), but Theo must go first to avoid merge conflicts.
- The `cloud:down` task in `Taskfile.cloud.yml` is off-limits for modification; auth teardown must rely on Terraform's own `destroy` path.

## Issue #10 Review — Modernize CI and deployment pipelines (2026-07-31)

### Verdict: APPROVE with advisory findings

Reviewed ci.yml, deploy-production.yml, build-and-deploy.yml deprecation, backend.tf (both stacks), bootstrap-remote-state.sh, docs/remote-state.md.

### Acceptance criteria mapping
- ✅ CI validates both Terraform stacks (fmt + init -backend=false + validate)
- ✅ .NET test suites for 4 of 5 test projects; Python compileall
- ✅ All 7 container images built and verified in CI matrix
- ✅ OIDC-only authentication; no long-lived credentials anywhere
- ✅ Remote state with azurerm backend, blob-lease locking, workspace-based env separation
- ✅ Production environment gate on deploy-infrastructure, deploy-apps, smoke
- ✅ Post-deployment smoke checks with actionable output and evidence artifact
- ✅ cloud:down untouched; edits surgical

### Advisory findings (non-blocking)
1. **Missing test project in CI** (`ci.yml`): `BankingAgent.Infrastructure.Tests` exists but is not exercised. Low risk since these tests would also run via solution-level `dotnet test`, but the per-project approach omits it. Recommend adding.
2. **No concurrency group on deploy-production.yml**: Two fast pushes to main can trigger parallel deploys. Blob lease will serialize Terraform, but image pushes could interleave. Recommend `concurrency: { group: production-deploy, cancel-in-progress: false }`.
3. **`build-and-push` job uses `secrets.*` without `environment:`**: The `TF_BACKEND_*` secrets are scoped to the `production` environment. Without `environment: production` on `build-and-push`, those secrets will be empty. This is a **functional issue** but the docs already note that a `ref:refs/heads/main` federated credential is required — the fix is either: (a) add `environment: production` to `build-and-push`, or (b) promote TF_BACKEND_* to repo-level secrets.
4. **Python agent tests** (`src/agents/python/tests/test_agents.py`): Only `compileall` is run; actual pytest suite is not invoked. Non-blocking since acceptance criteria says "Python compileall" is the current bar.
5. **`apps/variables.tf` has `app_name`, `region`, `image_tag`, `enable_service_auth`**: Region-only-input convention is satisfied at the infrastructure layer; apps stack legitimately needs `app_name` and `image_tag` as deploy-time inputs from the infrastructure output.

### Blocking concern
Finding #3 above is the only issue that could cause a real deployment failure. The `build-and-push` job accesses `secrets.TF_BACKEND_*` to init the infrastructure backend and read ACR_NAME, but it lacks `environment: production`. GitHub will not inject environment-scoped secrets into a job without the matching `environment:` key — the terraform init will fail with empty backend config.

**Recommended fix:** Add `environment: production` to the `build-and-push` job, OR restructure so ACR name is a repository variable instead of derived from Terraform state.

**However**, since this is a _configuration_ issue (fixable by promoting secrets to repo level as the docs partially suggest), and the overall design is sound and complete, I'm issuing a conditional **APPROVE** — the author must acknowledge and fix finding #3 before merge.

### Learnings
- Always verify that jobs referencing environment-scoped secrets actually declare that environment.
- Workspace-as-region is a clean pattern for single-variable environment separation.
- The deprecated-workflow pattern (no-op cron + notice job) is a good transition strategy.

## Issue #10 Final Review – Theo Revision (2026-07-31)

### Verdict: APPROVE

Theo's revision addresses all blocking concerns from the first review:
1. **`build-and-push` now declares `environment: production`** — environment-scoped secrets (`TF_BACKEND_*`) will be injected correctly.
2. **Concurrency group added** — `deploy-production` with `cancel-in-progress: false` prevents overlapping production deploys.
3. **`BankingAgent.Infrastructure.Tests` added to CI** — all 5 .NET test projects exercised.
4. **Python pytest suites added** — both `python-agent-tests` and `python-deployer-tests` jobs run actual test suites beyond compileall.
5. **OIDC comment corrected** — only `repo:<org>/<repo>:environment:production` subject is documented.

### Acceptance criteria map (all green)
| Criterion | Evidence |
|-----------|----------|
| CI validates both TF stacks | `terraform-infrastructure` + `terraform-apps` jobs |
| All .NET/Python tests in CI | 5 dotnet test steps + 2 pytest jobs + compileall |
| All 7 images built/verified | `build-images` matrix (7 entries, fail-fast: false) |
| OIDC only | `vars.*` for client/tenant/sub, `use_oidc=true` in backend, no `secrets.AZURE_CREDENTIALS` |
| Remote state documented | `docs/remote-state.md`, both `backend.tf` files, bootstrap script |
| Production approval gate | `environment: production` on all deploy/smoke jobs |
| Deploy sequence correct | build-and-push → deploy-infrastructure → deploy-apps → smoke |
| Smoke checks actionable | `smoke-mvp.py` + artifact upload |
| cloud:down unchanged | No modifications |
| Region-only TF input | infrastructure stack uses `region` only; apps adds `app_name`/`image_tag` (expected) |

### Advisory (non-blocking)
- The `terraform-apps` validate job uses `TF_VAR_app_name=ci-validation-placeholder` but doesn't set `TF_VAR_image_tag`. If `image_tag` has no default, validate may warn. Low risk — validate checks config syntax not values.

### Learnings
- Concurrency group on deploy workflows is essential for blob-lease-based locking; without it, concurrent pushes can fail non-deterministically.
- Splitting CI from deploy into separate workflow files improves auditability and secret-scope hygiene.

---

## Lessons from Issue #10 Review Cycle (2026-07-31)

Working as the primary reviewer across the full Issue #10 CI/CD modernization cycle taught important patterns:

1. **Environment secret scope is a common trap** — GitHub will not inject environment-scoped secrets into jobs that don't declare the matching `environment:` key. This can cause silent failures downstream (terraform init trying to read empty backend config). Always verify job-to-environment matching.

2. **Concurrency groups with blob-lease locking need explicit `cancel-in-progress: false`** — letting new deployments kill in-flight ones causes race conditions where one job succeeds (image push) while another rolls back (Terraform). The pattern is: `concurrency: { group: <production>, cancel-in-progress: false }` at workflow level.

3. **Revision-by-different-author validates independence** — asking Theo to fix Nia's blocking issues caught some gaps that fresh eyes naturally surface (e.g., Python pytest was advisory in first pass but became critical to acceptance in second pass). This is a good workflow for high-confidence code.

4. **Partial backend config pattern is genuinely elegant** — allowing Terraform to validate and format without credential injection makes credential-free CI possible while keeping live infrastructure state and blob-lease locking.

5. **Advisory findings often become requirements in follow-up revisions** — the first-pass advisor on Python pytest ("consider adding pytest jobs") became a hard requirement in the revision request, even though the initial verdict was conditional-APPROVE. Document advisories thoroughly so revisions know which are just-nice-to-have vs. strategic gaps.

## Issue #9 Design Review — Expand automated workflow and API test coverage (2026-07-31)

### Verdict: DESIGN REVIEW COMPLETE — Implementation split ready

### Existing coverage inventory (issues #7, #8, #10 already landed)
| Area | File | Tests | Coverage |
|------|------|-------|----------|
| Persistence (EF) | Infrastructure.Tests/EfWorkflowActionRepositoryTests | 4 | Approve/reject decision, idempotent retry, rollback on failure |
| Restart recovery | Infrastructure.Tests/WorkflowRestartRecoveryTests | 3 | Survive restart, competing claimers, stale draft resume |
| MCP reliability | Infrastructure.Tests/FoundryMcpClientReliabilityTests | 5 | Retry, non-transient, bounded attempts, timeout, cancellation |
| Approval concurrency | Application.Tests/WorkflowApprovalConcurrencyTests | 2 | Concurrent matching, concurrent conflicting |
| Repository contract | Application.Tests/WorkflowRepositoryContractTests | 7 | CRUD, stale version, chronological events |
| Workflow behavior | Application.Tests/WorkflowServiceCurrentBehaviorTests | 14 | All approval paths, idempotency, failure persistence, support cases |
| Demo scenarios | Application.Tests/DemoScenarioTests | 5 | Catalog, durable state, case outcomes, independence, disabled-mode |
| Telemetry | Application.Tests/WorkflowTelemetryTests | 1 | Correlated lifecycle spans |
| Evidence | Application.Tests/WorkflowEvidenceServiceTests | 4 | Valid upload, non-dispute rejection, content mismatch, limit |
| ProblemDetails | Api.Tests/ProblemDetailsContractTests | 7 | Validation, not-found, conflict, dependency failure, timeout, 500 |
| Auth contract | Api.Tests/AuthenticationContractTests | 10 | Anon 401, wrong role 403, valid role passes, health anon |
| Endpoint contract | Api.Tests/WorkflowEndpointContractTests | 8 | 404, 200, chronological events, conflict, idempotent, demo catalog |
| Evidence endpoint | Api.Tests/WorkflowEvidenceEndpointTests | 2 | Upload, download |
| WebUI | WebUi.Tests/DemoScenarioUiTests | 1 | Index exposes scenarios |
| WebUI handlers | WebUi.Tests/CorrelationIdHandlerTests, TokenDelegatingHandlerTests | 2+ | Token delegation |
| Python agents | python/tests/test_agents.py | 4 | Planner routing, dispute approval, suspicious activity, model status |
| Python deployer | deployer/test_deploy.py | exists | Deploy utility tests |

### Gap analysis against issue #9 acceptance criteria

**1. Persistence, authorization, approval/rejection, idempotency, support-case, ProblemDetails** — MOSTLY COVERED.
- Gap: No test for `SupportCase` persistence via full API round-trip (existing tests are at application layer only).
- Gap: No test that approval idempotency returns identical response body shape.
- Gap: No negative ProblemDetails test for oversized evidence upload at API level.

**2. Restart tests proving durable recovery without duplicate actions** — COVERED by WorkflowRestartRecoveryTests (3 tests). Minor gap: no test proving restart after approval-but-before-action-execution doesn't duplicate the action.

**3. Hosted-agent tests (success, failure, timeout, boundary contracts with deterministic doubles)** — MAJOR GAP.
- `app/hosted.py` has zero test coverage. No tests for the `InvocationAgentServerHost` invoke handler.
- No timeout test for hosted agent invocations.
- No failure/error-path test for malformed requests.
- No boundary contract test for request/response schema validation.

**4. E2E tests in CI before production deployment** — MAJOR GAP.
- `deploy-production.yml` has post-deployment smoke only; no pre-deployment E2E.
- No `WebApplicationFactory`-based E2E test that exercises a representative workflow path through API → service → persistence → approval → completion.
- No CI job that gates deployment on E2E.

**5. Complete suite reliable in local and CI quality gates** — PARTIAL.
- `Taskfile.test.yml` covers all .NET and Python test commands. Missing: E2E task, no `test:all` aggregate, no documented local quality gate checklist.

### Implementation split

**Owner: Theo (Backend) — primary implementer for all .NET test work**
Rationale: All gaps are in .NET test projects or Python agent tests. Splitting .NET test work across multiple agents creates merge conflicts in shared test infrastructure (TestOrchestratorHost, SqliteTestProvider, csproj references). One owner minimizes coordination cost.

**Files Theo creates/modifies:**
1. `tests/BankingAgent.Api.Tests/WorkflowE2eTests.cs` — NEW. Full lifecycle E2E: POST workflow → GET status → POST approval → GET final state with support case. Uses `WebApplicationFactory` with in-memory SQLite. Covers the "representative API workflow path against production-like dependencies" criterion. Deterministic (no live Azure).
2. `tests/BankingAgent.Api.Tests/WorkflowEndpointContractTests.cs` — ADD test for idempotent approval returning identical response body.
3. `tests/BankingAgent.Api.Tests/ProblemDetailsContractTests.cs` — ADD test for oversized evidence upload returning 413/422 ProblemDetails.
4. `tests/BankingAgent.Infrastructure.Tests/WorkflowRestartRecoveryTests.cs` — ADD test for restart-after-approval-before-action scenario.
5. `tests/BankingAgent.Api.Tests/TestOrchestratorHost.cs` — Minimal refactors if needed for E2E composition.

**Owner: Lumen (AI Platform) — primary implementer for Python hosted-agent tests**
Rationale: Hosted agent is Python/LangGraph domain; Lumen owns this.

**Files Lumen creates/modifies:**
1. `src/agents/python/tests/test_hosted.py` — NEW. Tests for `app/hosted.py`:
   - Success path: valid `AgentRequest` → valid `AgentResult` JSON response.
   - Failure: malformed JSON → appropriate error response.
   - Timeout: mock graph that exceeds deadline → timeout behavior.
   - Boundary contract: request/response schema validation with deterministic model doubles (mock `ainvoke`).
2. `src/agents/python/tests/conftest.py` — NEW if needed for shared fixtures.

**Owner: Nia (QA & DevOps) — CI/Taskfile integration and documentation**
Rationale: Nia owns CI pipeline and quality gate docs.

**Files Nia creates/modifies:**
1. `.github/workflows/ci.yml` — ADD E2E test step after existing API integration tests (same `dotnet` job, new step targeting `BankingAgent.Api.Tests --filter Category=E2E` or similar).
2. `.github/workflows/deploy-production.yml` — ADD `needs: [ci]` or equivalent gate ensuring CI (including E2E) passes before any deploy job starts (already has `workflow_run` trigger from CI, but verify the E2E tests are included).
3. `tasks/Taskfile.test.yml` — ADD `test:e2e` task, ADD `test:all` aggregate task, ADD `test:hosted` task for Python hosted agent tests.
4. `docs/testing.md` — NEW. Document local quality gate checklist: what to run, expected pass criteria, how to add new tests.

### Conflict boundaries
- **Theo and Lumen**: Zero overlap. Theo works in `tests/` (.NET), Lumen in `src/agents/python/tests/` (Python). No shared files.
- **Theo and Nia**: Nia only modifies CI/Taskfile/docs. Theo only modifies test `.cs` files. Nia's CI changes reference Theo's new test category but don't edit the same files.
- **Lumen and Nia**: Nia adds `test:hosted` Taskfile entry; Lumen creates the Python test file it invokes. No file overlap.

### Test harness architecture
- .NET E2E: `WebApplicationFactory<Program>` with SQLite in-memory provider (existing `SqliteTestProvider` pattern). Deterministic MCP doubles already established in `WorkflowEndpointContractTests`. No new test infrastructure needed.
- Python hosted: `unittest.IsolatedAsyncioTestCase` (existing pattern in `test_agents.py`). Mock `get_agent_graph` to return deterministic graph. Use `starlette.testclient.TestClient` for HTTP-level tests of `app`.
- No live Azure, no external network calls, no flaky timing.

### Validation commands
```
# Local quality gate (all must pass)
task test:unit           # Existing .NET + infra tests
task test:e2e            # New E2E lifecycle tests
task test:python-agents  # Existing + new hosted agent tests
task test:python-deployer
dotnet format banking-agent.sln --verify-no-changes
```

### Reviewer gates
- Theo's .NET tests → reviewed by Aria (architecture/contract compliance)
- Lumen's Python tests → reviewed by Theo (cross-boundary contract alignment)
- Nia's CI/docs → reviewed by Aria (pipeline correctness, no weakened security)

### Security guardrails
- No test-only endpoints in production code
- E2E tests use `WebApplicationFactory` — the test host is in-process, not a deployed service
- No authentication bypass; E2E tests use the same `FakeJwtBearerHandler` pattern as existing API tests
- `deploy-production.yml` already triggers only after CI completes; E2E in CI provides the gate
- No `cloud:down` modifications

## Issue #9 Review — Test Coverage, Persistence, E2E, Hosted-Agent Contracts (2026-07-31)

### Artifacts reviewed
- `.github/workflows/ci.yml` (Nia)
- `.github/workflows/deploy-production.yml` (Nia)
- `tasks/Taskfile.test.yml` (Nia)
- `docs/testing.md` (Nia)
- `README.md` (Nia)
- `tests/BankingAgent.Api.Tests/ProblemDetailsContractTests.cs` (Theo)
- `tests/BankingAgent.Api.Tests/TestOrchestratorHost.cs` (Theo)
- `tests/BankingAgent.Api.Tests/WorkflowEndpointContractTests.cs` (Theo)
- `tests/BankingAgent.Api.Tests/WorkflowE2eTests.cs` (Theo)
- `tests/BankingAgent.Infrastructure.Tests/WorkflowRestartRecoveryTests.cs` (Theo)
- `src/agents/python/app/hosted.py` (Lumen)
- `src/agents/python/tests/test_hosted.py` (Lumen)

### Learnings
- CI `dotnet-e2e` job runs E2E tests with `--filter Category=E2E`, but the `dotnet` job runs the same project without exclusion, causing E2E tests to execute twice. This is explicitly documented as intentional in docs/testing.md. Not a blocking issue but adds CI time.
- The `python-hosted-tests` CI job uses an `if [ -f ... ]` guard that silently skips when the file is absent. Now that `test_hosted.py` exists, this is moot for current state but the guard should be removed to prevent future regressions where the file is accidentally deleted.
- `WorkflowRestartRecoveryTests` uses `Path.GetTempPath()` for SQLite files — acceptable for CI runners but noted as an OS-specific detail. Files are cleaned up in `DisposeAsync`.
- TestOrchestratorHost has two constructors: one for mock-based contract tests, one for real-service E2E tests. Clean separation, good pattern.
- The E2E tests use in-memory repositories (not SQLite), while Infrastructure.Tests use real SQLite. This is an intentional layering decision: E2E proves HTTP→service→repo chain, Infrastructure.Tests prove EF↔SQLite persistence. No duplication.

## Async Workflow Design Review (2026-07-31)

### Verdict: APPROVE — durable async via existing primitives

### Key Design Choices
- POST /api/v1/workflows returns 202 with Location header after persisting Draft (no agent call in request path).
- Execution is triggered by existing `WorkflowRecoveryWorker` claiming Draft/Recovering rows via atomic versioned UPDATE.
- An immediate best-effort `Task.Run` nudge after Draft persist reduces latency to <5s in happy path; the periodic scanner guarantees delivery.
- No new states, no schema migration, no message broker.
- UI uses exponential-backoff polling (1s→10s cap, 90s timeout) with accessible stage indicators.
- Approval remains synchronous and idempotent within the approval request.

### Ownership Split
- **Theo:** API (202 + Location), application (split Start/Execute), UI (polling, compact workspace, stages, timeline).
- **Nia:** All test files, docs updates, accessibility verification.

### Learnings
- The existing Draft + Recovering + ClaimNextAsync pattern is already a complete durable-execution primitive. The only missing piece was decoupling the POST response from execution completion.
- Fire-and-forget `Task.Run` as optimization + periodic background scan as guarantee is a well-known pattern that avoids message broker complexity at prototype scale.
- 202 Accepted is a breaking API change requiring coordinated webui/orchestrator deployment; document this clearly for CI.

## Async Workflow Final Review (2026-07-31)

### Verdict: APPROVE
- 162/162 tests pass; build clean; 8/8 review criteria met.
- Theo's implementation (202 semantics, trigger, WorkflowService split, UI polling) is correct.
- Nia's test coverage (44 new tests, smoke rewrite, docs) is thorough.
- Residual risks: version-based stage proxy, single-claim trigger, blind WebUI smoke sleep — all non-blocking for prototype.

### Learnings
- The `ExpectsEvidence` pattern (defer trigger until evidence upload) is an effective race-condition elimination without coordination primitives. Worth reusing for any two-phase submit pattern.
- `RecoverAsync` accepting terminal states as idempotent no-ops is the right design — it means the trigger and periodic worker can safely overlap without error-shaped outcomes.
- StaleAfterSeconds at 10s is aggressive for production; operators should tune this per environment.

## Evidence-Files Optional Gate (2026-07-31)

### Design Review
- **Root cause confirmed:** `List<IFormFile>` (non-nullable) triggers `data-val-required`; jQuery validation cancels submit after JS sets `is-loading`.
- **Approved fix:** Make `EvidenceFiles` nullable (`List<IFormFile>?`), coalesce to `[]` in `OnPostAsync`, reorder JS to check `jqForm.valid()` before entering loading state.
- **Test approach:** PageModel unit test proving null-evidence POST succeeds. No Playwright — existing suite uses direct model invocation with Moq/FakeHttpMessageHandler.
- **Ownership:** Theo → model + Razor + JS; Nia → unit test + manual verification checklist.
- **Decision doc:** `.squad/decisions/inbox/aria-optional-evidence-gate.md`
- **Verdict:** APPROVED

## 2026-07-31 — Final Review: Evidence-Optional Feature

**Verdict: REJECT** (2 items)

1. **Server-side fix (Theo) ✅** — `List<IFormFile>?` + `?? []` coalescing correct.
2. **JS validation ordering ✅** — busy state gated behind validation; `e.preventDefault()` on invalid.
3. **Skipped test ❌** — `OnPostAsync_WithNullEvidenceFiles` still has `[Fact(Skip=...)]` despite Theo's fix landing. Must be unskipped. (Nia-authored → revise by Theo/Lumen)
4. **No real HTTP POST test ❌** — JS tests use jsdom `defaultPrevented` assertions only; user explicitly required an observed HTTP POST/request. Need `WebApplicationFactory` + `HttpClient` integration test. (Nia-authored → revise by Theo/Lumen)

Full rationale in `.squad/decisions/inbox/aria-evidence-final-review.md`.
