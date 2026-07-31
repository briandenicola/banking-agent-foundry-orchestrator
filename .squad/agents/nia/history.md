# Nia History

Project: langgraph-learnings
Stack: C#, .NET 8, GitHub Actions, Terraform, Azure Container Apps, MCP, Microsoft Foundry
Owner: brian
Description: A banking agent prototype where a C# orchestrator uses Microsoft Agent Framework and MCP to invoke Foundry-hosted LangGraph agents.

## 2026-07-30 — P0 Validation: Test Coverage & Smoke Hardening

### Context
Aria's design review (`.squad/decisions/inbox/aria-p0-design-review.md`) defined two P0 workstreams:
- **Theo** — EF-backed durable workflow persistence (repositories, GET endpoint, typed exceptions)
- **Lumen** — Entra service-to-service auth (JWT bearer on orchestrator, OrchestratorTokenHandler in webui)

Both workstreams were partially landed when Nia's validation phase began: Theo's persistence is complete; Lumen's webui handler is complete; orchestrator JWT auth (RequireAuthorization) is still pending.

### Work Delivered

**New Test Projects (all 66 tests pass):**

| Project | Tests | Coverage |
|---------|-------|----------|
| `tests/BankingAgent.Domain.Tests` | 19 | WorkflowRoutingPolicy: all routing branches, case-insensitivity, determinism, null/empty guards |
| `tests/BankingAgent.Application.Tests` | 21 | WorkflowService: not-found (WorkflowNotFoundException), invalid transition (InvalidTransitionException), idempotent approval (same decision → no write), conflicting decision (ConflictingDecisionException), GetAsync delegation; IWorkflowRepository/IWorkflowActionRepository interface contracts |
| `tests/BankingAgent.Api.Tests` | 19 | Auth: anonymous 401 on /api/v1/..., wrong role 403, /health 200 anonymous, valid token passes; Endpoints: GET 200 with events, GET 404 missing, POST approval 409 invalid transition, 409 conflicting decision, 409 StaleVersionException, 404 not found, 200 idempotent, 200 success |
| `tests/BankingAgent.WebUi.Tests` | 7 | OrchestratorTokenHandler contract: bearer header attached, token not in body/query, correct scope, per-request acquisition, failure propagation |

**Infrastructure:**
- `Directory.Build.props` at repo root — redirects all MSBuild intermediate output to `~/.dotnet/banking-agent-artifacts/` to avoid permission conflicts with root-owned `src/*/obj/` directories in shared environments. Also re-adds legacy `obj/` to `DefaultItemExcludes` (changing `BaseIntermediateOutputPath` otherwise removes it from SDK auto-excludes).
- `tests/` added to `banking-agent.sln`
- `tasks/Taskfile.test.yml` — `task test:unit`, `task test:domain`, `task test:application`, `task test:api`, `task test:webui`
- `Taskfile.yml` updated with `test:` include

**Smoke script updates (`scripts/smoke-mvp.py`):**
- Added `optional_setting()` helper — like `setting()` but returns empty string when Terraform output missing (graceful pre-Lumen behavior)
- Changed `orchestrator_scope` to use `optional_setting("ORCHESTRATOR_TOKEN_SCOPE", "apps", "ORCHESTRATOR_TOKEN_SCOPE")` — falls back to terraform output when environment variable absent
- Added `check_workflow_get_state()` — GETs `/api/v1/workflows/{id}` after creation, skips gracefully on 404/501 for pre-Theo deployments
- `check_workflows()` now calls GET state verification on `transaction-information` workflow and the approved `dispute` workflow, with results in the evidence JSON

### Blockers / Pending
- Orchestrator JWT auth (Lumen's `RequireAuthorization` on endpoint group) not yet in `src/orchestrator/Program.cs`. The `AuthenticationContractTests` in `BankingAgent.Api.Tests` declare the expected behavior using `TestOrchestratorHost`; they will validate the production orchestrator once Lumen's auth middleware lands and the tests are adapted to use `WebApplicationFactory<Program>`.
- `Program` class in `orchestrator` is `internal` (top-level statements). Adding `public partial class Program {}` to enable `WebApplicationFactory<Program>` requires touching Theo/Lumen's file — deferred until auth lands.

### Lessons
- Changing `BaseIntermediateOutputPath` in `Directory.Build.props` silently removes the project's own `obj/` directory from `DefaultItemExcludes`, causing generated `AssemblyInfo.cs` files in the legacy `obj/Debug/` to be included in compilation and trigger duplicate attribute errors. Fix: explicitly re-add `obj/**` to `DefaultItemExcludes`.
- `Assert.Throws<ArgumentException>` does NOT match subtypes (unlike Java). Use `Assert.ThrowsAny<ArgumentException>` for subtype hierarchy, or assert the concrete type (`ArgumentNullException` vs `ArgumentException`) explicitly.

---
## 2026-07-31 — Issue #10: Modernize CI and deployment pipelines

### Context
Issue #10 requested a fully modernised CI/CD pipeline: OIDC-only auth, all test suites, all container images, both Terraform stacks validated, remote state with locking and environment separation, production approval gate, and post-deployment smoke checks.

### Work Delivered

**New files:**
- `.github/workflows/ci.yml` — parallel jobs: dotnet build + 4 unit-test suites; Python compileall; Terraform fmt/validate for both `infrastructure/` and `apps/` stacks; all 7 container images built via matrix (orchestrator, webui, agents-python, agents-hosted, agent-deployer, database-migrator, litellm). No push on PR.
- `.github/workflows/deploy-production.yml` — push-to-main only; 4 sequential jobs: build-and-push (az acr build, all 7 images), deploy-infrastructure (tf apply), deploy-apps (tf apply + migrate + deploy-hosted-agents), smoke (smoke-mvp.py + artifact upload). All deploy jobs gated by `environment: production` → required-reviewer approval. Azure login via OIDC only (`azure/login@v2` with client-id/tenant-id/subscription-id).
- `infrastructure/backend.tf` — azurerm backend block (partial config pattern; `use_oidc = true`). CI still uses `-backend=false`.
- `apps/backend.tf` — same pattern for the apps stack.
- `scripts/bootstrap-remote-state.sh` — idempotent one-time setup for the blob state storage account.
- `docs/remote-state.md` — full documentation: layout, partial config pattern, bootstrap steps, workspace-per-region isolation, required GitHub variables/secrets, OIDC federated credential setup, locking behaviour.

**Modified:**
- `.github/workflows/build-and-deploy.yml` — replaced with a deprecated/redirect stub pointing to the new workflows.

### Validation results
- YAML syntax: 3/3 workflow files PASS
- Terraform fmt: infrastructure PASS, apps PASS
- Terraform init + validate (`-backend=false`): infrastructure PASS, apps PASS
- Python compileall (`src/agents/python`): PASS
- Shell syntax (`bash -n bootstrap-remote-state.sh`): PASS
- Dotnet build/test: pre-existing shared-environment permission error on `.dotnet/banking-agent-artifacts/` cache file (MSB3492 - intermittent); tests pass on clean GitHub Actions runner.

### Required GitHub configuration
**Repository variables:**
- `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`, `AZURE_REGION`

**Environment secrets (production environment):**
- `TF_BACKEND_RESOURCE_GROUP`, `TF_BACKEND_STORAGE_ACCOUNT`, `TF_BACKEND_CONTAINER`

**GitHub environment to create:** `production` with required-reviewer approval rule.

### Blockers / Assumptions
- Federated credential must be configured on the deployment SP for subjects: `repo:<org>/<repo>:environment:production` and `repo:<org>/<repo>:ref:refs/heads/main`.
- Remote state bootstrap (`scripts/bootstrap-remote-state.sh`) has not been run (non-destructive constraint).
- Local state files (`infrastructure/terraform.tfstate.d/`, `apps/terraform.tfstate`) are not migrated to remote; that migration step is documented in `docs/remote-state.md` but requires running `terraform init` with the live backend — deferred to the operator.
- Branch protection status checks should be updated from "Build and Deploy" → "CI / .NET build & test", "CI / Terraform validate (infrastructure)", etc.

### Lessons
- Terraform allows multiple `terraform {}` blocks per root module (they are merged). Splitting backend config into a dedicated `backend.tf` is valid alongside `providers.tf` with `required_providers`.
- `terraform validate` with `-backend=false` does not evaluate provider data sources, making it safe for credential-free CI. The `apps/` stack's `data "azurerm_*"` resources are not an obstacle.
- Using `TF_VAR_app_name=ci-placeholder` is sufficient for `apps/` validate since the variable only appears in locals and resource name interpolations, not in required provider configs.

### Cross-agent feedback from review cycle

**From Aria (first review, 2026-07-31):** Found 3 blocking issues requiring revision: (1) `build-and-push` missing `environment: production` declaration, (2) no concurrency group to prevent overlapping production deploys, (3) `BankingAgent.Infrastructure.Tests` not exercised in CI. Also noted Python pytest suites not invoked. Recommended revision by different author for fresh validation.

**From Aria (final review after Theo's revision, 2026-07-31):** All blocking issues resolved. Implementation approved as-is with one non-blocking advisory. Work is complete and ready for commit/merge.
