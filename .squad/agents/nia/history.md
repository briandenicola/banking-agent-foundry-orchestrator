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
