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
