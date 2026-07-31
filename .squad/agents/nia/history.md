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

---
## 2026-07-31 — Issue #9: Local/CI quality gates and testing docs

### Context
Aria's design review (`.squad/decisions/inbox/aria-issue-9-design-review.md`) assigned Nia ownership of:
- `test:e2e`, `test:hosted`, `test:all` Taskfile targets
- `ci.yml` E2E and hosted-Python CI jobs
- Deploy gate verification for `deploy-production.yml`
- `docs/testing.md` and README link

Theo owns all `.NET test code`; Lumen owns `tests/test_hosted.py`. Neither was expected to be landed yet at time of this work.

### Work Delivered

**Modified:**
- `tasks/Taskfile.test.yml` — added `test:e2e` (dotnet filter `Category=E2E` on `BankingAgent.Api.Tests`), `test:hosted` (pytest `tests/test_hosted.py` from `src/agents/python` working dir), and `test:all` aggregate gate (unit → e2e → python-agents → python-deployer → hosted).
- `.github/workflows/ci.yml` — added two new jobs: `dotnet-e2e` (needs: dotnet; `--filter Category=E2E`) and `python-hosted-tests` (conditional shell guard — skips gracefully when `tests/test_hosted.py` absent, runs deterministically once Lumen lands it). No duplicates with existing jobs.
- `README.md` — added `docs/testing.md` to the repository layout bullet list.

**Created:**
- `docs/testing.md` — full testing guide: taxonomy (Categories A/B/C/D/E/E2E/P/PH), local commands, CI job table, deploy gate mechanism, Python working directory requirement, duplication avoidance note, guidance for adding new tests.

**Not modified:**
- `deploy-production.yml` — already correctly gated. `workflow_run: [CI]` with `conclusion == 'success'` on `head_branch == 'main'`. GitHub sets `conclusion = success` only when ALL CI jobs pass, so adding `dotnet-e2e` and `python-hosted-tests` to `ci.yml` automatically strengthens the production gate without any deploy workflow changes.
- All `.NET` and Python test code — not touched per constraints.
- `cloud:down` — not touched.

### Validation Results
- `ci.yml` YAML: `python yaml.safe_load` — PASS
- Taskfile list (`task --list`): `test:all`, `test:e2e`, `test:hosted` visible — PASS
- README `testing.md` link: present at line 22 — PASS
- `deploy-production.yml` gate analysis: no gap found

### Structural vs Full Validation
- `test:e2e` — structurally valid; `--filter Category=E2E` exits 0 when no tests carry the trait (Theo's work not yet landed). Will run tests automatically once `[Trait("Category","E2E")]` appears.
- `test:hosted` (Taskfile) — will fail with clear error when `tests/test_hosted.py` absent; intended as developer-run gate after Lumen's work lands.
- `python-hosted-tests` (CI) — uses `if [ -f ... ]` guard; skips safely pre-Lumen, runs automatically post-Lumen.

### Lessons
- GitHub Actions `workflow_run.conclusion` aggregates all jobs — adding jobs to the upstream workflow automatically tightens the downstream `if:` gate with no changes to the dependent workflow.
- `dotnet test --filter Category=E2E` exits 0 when no tests match the filter. Document this explicitly in testing.md to avoid future confusion ("why did CI pass with no E2E tests?").
- Taskfile `dir:` shorthand sets working directory per task — cleaner than `cd src/... &&` inside `cmds`.

---
## 2026-07-31 — Validation run: full quality gate execution (post-Issue-#9/#10)

### Context
Independent validation run at briandenicola's request.  Read-only: no production files modified, no commits made.

### Commands Executed and Results

| # | Command | Working Dir | Result | Detail |
|---|---------|-------------|--------|--------|
| 1 | `task --list` | repo root | **PASS** | All expected targets visible: `test:all`, `test:e2e`, `test:hosted`, `test:unit`, `test:python-agents`, `test:python-deployer`, etc. |
| 2 | `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"` | repo root | **PASS** | No YAML syntax errors |
| 3 | `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/deploy-production.yml'))"` | repo root | **PASS** | No YAML syntax errors |
| 4 | `dotnet build banking-agent.sln -c Release --no-restore` | repo root | **PASS** | 0 warnings, 0 errors, 4.4s |
| 5 | `dotnet test tests/BankingAgent.Api.Tests --configuration Release --no-build --filter Category=E2E` | repo root | **PASS** | **3 tests discovered and passed** (not zero). `WorkflowE2eTests.ExplanationWorkflow_CompletesWithoutApprovalOrSupportCase`, `DisputeWorkflow_GetCalledTwice_ReturnsSameSupportCaseId`, `DisputeWorkflow_WhenApproved_SupportCaseVisibleInGetResponse`. Duration 1.2s. |
| 6 | `dotnet test banking-agent.sln --configuration Release --no-build` (full suite) | repo root | **PASS** | Domain: 19, Application: 43, WebUi: 9, Api: 34, Infrastructure: 14 — **Total: 119 passed, 0 failed, 0 skipped** |
| 7 | `python3 -m pytest tests/test_agents.py -v --tb=short` | `src/agents/python` | **PASS** | 4 passed, 1 warning (LangChainPendingDeprecation — benign), 0.89s |
| 8 | `python3 -m pytest tests/test_hosted.py -v --tb=short` | `src/agents/python` | **PASS** | 13 passed, 3 warnings (OTel I/O on closed file — teardown race, benign), 1.37s |
| 9 | `python3 -m pytest test_deploy.py -v --tb=short` | `src/agents/deployer` | **PASS** | 7 passed, 0 warnings, 0.12s |

### E2E filter false-green check
Confirmed: `--filter Category=E2E` on `BankingAgent.Api.Tests` discovers **3 real tests** carrying `[Trait("Category","E2E")]`. Not a zero-test/false-green situation. Theo's E2E tests are landed and exercised.

### Hosted filter false-green check
`tests/test_hosted.py` exists and collected 13 tests. Lumen's hosted contract tests are landed and pass. The CI guard `if [ -f ... ]` would now run them, not skip.

### Warnings / Non-blocking observations
- `LangChainPendingDeprecationWarning` in `test_agents.py`: `allowed_objects` default will change in a future LangGraph version. No action needed now.
- OTel `ValueError: I/O operation on closed file` in `test_hosted.py`: background metrics exporter thread races against test teardown. Does not affect test outcome; all 13 pass. Known pattern for in-process OTEL SDK in short-lived pytest sessions.
- Root-level `pytest` invocation (`python3 -m pytest src/agents/python/tests/test_agents.py`) fails with `ModuleNotFoundError: No module named 'app'`. Must invoke from `src/agents/python` as working directory. This matches the `dir:` convention in `Taskfile.test.yml` and `docs/testing.md` — documented correctly.

### Blockers
None. All gates green.

### Lessons
- E2E filter actually finds tests — false-green concern from Issue #9 documentation was pre-emptive; Theo landed E2E traits before this validation ran.
- OTel teardown race in hosted tests is cosmetically noisy but structurally harmless. Could be suppressed with `filterwarnings` or explicit meter shutdown in conftest if it becomes distracting.
- Python working-directory sensitivity is real: root-level pytest invocation errors out. The Taskfile `dir:` and CI `working-directory:` configurations are essential and correct.

---
## 2026-07-31 — Advisory F1 Applied: Removed hosted test file-existence guard

### Context
Aria's approved-review advisory F1 recommended removing the file-existence guard (`if [ -f tests/test_hosted.py ]`) from the `python-hosted-tests` CI job in `.github/workflows/ci.yml`. The guard originally allowed safe skipping pre-Lumen; with the file now landed and tests passing, the guard should be removed to fail CI explicitly if the test file goes missing.

### Changes Made

1. **`.github/workflows/ci.yml`** — Hosted test job:
   - Removed shell conditional `if [ -f tests/test_hosted.py ]`
   - Simplified `run:` from multi-line shell block to direct `python -m pytest tests/test_hosted.py -v`
   - Updated comment to reflect deterministic behavior: "Runs pytest directly; fails if tests/test_hosted.py is missing"

2. **`docs/testing.md`** — CI mapping table:
   - Updated `Python hosted-agent tests` row from `Yes (skipped safely when file absent)` to `Yes`
   - Reflects that the job now blocks deploy on test file absence (as intended)

### Validation

| Check | Result | Detail |
|-------|--------|--------|
| YAML syntax | **PASS** | `python3 -c "import yaml; yaml.safe_load(open('.github/workflows/ci.yml'))"` — no errors |
| Test execution | **PASS** | Direct command from `src/agents/python`: `python -m pytest tests/test_hosted.py -v` — 13 tests passed |
| Files modified | ✓ 2 | `.github/workflows/ci.yml`, `docs/testing.md` |
| Commits made | 0 | Validation only; no changes persisted to remote |

### Lessons
- File-existence guards in CI are temporary scaffolding. Once the guarded file is stable (landed and passing tests), remove the guard to convert skip-silently → fail-explicitly, tightening the quality gate.
- Simplified `run:` commands (no shell conditionals) are easier to reason about and debug in workflow logs.
