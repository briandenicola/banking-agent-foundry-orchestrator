# Testing Guide

This document describes the test taxonomy, local commands, CI mapping, prerequisites, and acceptance scenarios for the Banking Agent prototype.

## Prerequisites

### System packages

| Dependency | Minimum | Notes |
|---|---|---|
| .NET SDK | 10.0 | `dotnet --version` |
| Python | 3.12 | `python --version` |
| SQLite (system) | 3.x | Required for E2E tests via `Microsoft.Data.Sqlite`. On Ubuntu: `sudo apt-get install libsqlite3-dev`. On macOS: included. Do **not** bundle a separate SQLite binary — the .NET package resolves the system library. |
| Task | any | `task --version` — install from [taskfile.dev](https://taskfile.dev/installation/) |

### Install Python dependencies

Python tests must run from the `src/agents/python` working directory so that relative imports resolve correctly.

```bash
cd src/agents/python
python -m pip install -r requirements.txt pytest
```

---

## Test taxonomy

### Category A — Domain unit tests (`BankingAgent.Domain.Tests`)

Pure business-logic tests. No I/O, no framework dependencies. Cover:

- `WorkflowRoutingPolicy`: all routing branches, case-insensitivity, determinism, null/empty guards.

### Category B — Application unit tests (`BankingAgent.Application.Tests`)

Service-layer tests with in-memory fakes. Cover:

- `WorkflowService`: not-found (`WorkflowNotFoundException`), invalid transition (`InvalidTransitionException`), idempotent approval (same decision → no write), conflicting decision (`ConflictingDecisionException`), `GetAsync` delegation.
- Repository and action-repository interface contracts.

### Category C — API contract tests (`BankingAgent.Api.Tests`)

In-process tests via `TestOrchestratorHost`. Cover:

- **Auth contracts**: anonymous → 401, wrong role → 403, `/health` anonymous → 200, valid token → 200.
- **Endpoint contracts**: GET workflow 200/404, POST approval 200/409 (invalid transition, conflicting decision, stale version, not found, idempotent).

### Category D — Web UI token handler (`BankingAgent.WebUi.Tests`)

In-process tests for `OrchestratorTokenHandler`. Cover:

- Bearer header attached on every request, token not leaked in body or query string, correct scope, per-request token acquisition, failure propagation.

### Category E — Infrastructure / persistence (`BankingAgent.Infrastructure.Tests`)

Integration tests for EF Core repositories, MCP reliability patterns, seed data contracts, and recovery atomicity. Use system SQLite databases; no PostgreSQL server is required locally.

| Test | What it verifies |
|---|---|
| `CompetingRecoveryClaimers_OnlyOneClaimsStaleWorkflow` | Exactly one of two concurrent `ClaimNextAsync` callers succeeds |
| `TwoConcurrentWorkers_BothSeeNewDraft_OnlyOneClaimsIt` | New Draft claimed atomically by only one worker |
| `ImmediateClaim_TargetsOnlyRequestedDraft_AndCannotReclaimRecoveringWorkflow` | Immediate trigger claims only its requested Draft and cannot steal active recovery work |
| `AfterClaim_RecoveringWorkflow_HasRecoveryClaimedEvent` | `workflow.recovery_claimed` event emitted on claim |
| `StaleDraft_ResumesToTerminalStateAfterRestart` | Draft → claim → RecoverAsync reaches terminal state |
| `RecoverAsync_CompletedWorkflow_DoesNotReprocess` | RecoverAsync on Completed returns current state (no duplicate execution) |
| `WorkflowAndApprovedAction_SurviveContextRestartWithoutDuplicates` | Support case and action count = 1 after restart + duplicate approval |

### Category E2E — End-to-end lifecycle (`BankingAgent.Api.Tests`, filter `Category=E2E`)

Production-like in-process tests use the real HTTP endpoints, `WorkflowService`, EF repositories, evidence service, JWT authorization policy, and Web UI `IndexModel`. System SQLite replaces PostgreSQL and deterministic MCP doubles replace live Foundry calls.

| Scenario | Steps | Expected result |
|---|---|---|
| POST returns 202 + Location | POST `/api/v1/workflows` | 202 Accepted, `Location` header, body `status: "Draft"` |
| Approved dispute with evidence | POST (202) → RecoverAsync → upload PNG → approve → GET | Completed workflow has one durable support case and evidence record |
| Rejected dispute | POST (202) → RecoverAsync → reject → GET | Rejected workflow has no support case |
| Idempotent approval | POST (202) → RecoverAsync → approve twice | Responses match and only one decision/action exists |
| Polling lifecycle | POST → GET (Draft) → RecoverAsync → GET (terminal) | Terminal status reached via polling |
| Evidence before specialist | POST (202) → upload evidence → RecoverAsync | Evidence persists through recovery |
| Planner failure | POST (202) → RecoverAsync (planner throws) | `Failed` status with `workflow.failed` event |
| Web UI path | Submit through `IndexModel` → API (202) → RecoverAsync → reload | UI displays the EF-persisted terminal workflow |

Restart recovery and duplicate-action behavior use recreated EF contexts in `WorkflowRestartRecoveryTests`. Oversized evidence and standardized ProblemDetails are API contract tests.

### Category E2E-Async — Async lifecycle tests (`BankingAgent.Api.Tests`, filter `Category=E2E`)

In `WorkflowAsyncLifecycleTests`, the full 202 → Draft → RecoverAsync → terminal path is exercised against a real in-process stack. Terminal polling statuses (`Completed`, `Failed`, `Rejected`, `WaitingForApproval`) and the `RecoverAsync` idempotency contract are verified.

| Scenario | Expected result |
|---|---|
| POST 202 + Location + Draft body | 202 Accepted, `Location` header set, `status: "Draft"` in body |
| Draft visible via GET immediately | Draft workflow retrievable before execution |
| Polling after RecoverAsync | Terminal status reached |
| Events increase after execution | GET returns more events after RecoverAsync |
| Planner failure via RecoverAsync | `Failed` status, `workflow.failed` event persisted |
| WaitingForApproval is polling terminal | Status in `PollingTerminalStatuses` set |
| Approval resume to Completed | Approve WaitingForApproval → Completed |
| Evidence attached before specialist | Evidence count = 1 after RecoverAsync |
| Evidence survives recovery | Evidence visible in GET after execution |
| Support case in GET after approval | `supportCase` non-null in response |

### Category P — Python agent unit tests (`src/agents/python/tests/`)

Pure Python unit tests, no live model calls. Cover:

- `test_agents.py` (`AgentGraphTests`): routing decisions across all agent types, approval flags, deterministic planning output using patched model calls.

### Category PH — Python hosted-agent contract tests (`src/agents/python/tests/test_hosted.py`)

Tests for the Foundry Hosted Agent invocation contract (`app/hosted.py`). Cover:

- Successful invocation with deterministic double.
- Failure propagation.
- Timeout handling.
- Boundary contracts for `InvocationAgentServerHost`.

---

## Local commands

### Run all unit tests (no live Azure)

```bash
task test:unit
```

### Run E2E tests

```bash
task test:e2e
```

The task first verifies that `WorkflowE2eTests` are discoverable, then runs:

```bash
dotnet test tests/BankingAgent.Api.Tests \
  --configuration Release \
  --filter Category=E2E \
  --logger "console;verbosity=normal"
```

### Run Python hosted tests

```bash
task test:hosted
```

Working directory is `src/agents/python`. The command is:

```bash
cd src/agents/python
python -m pytest tests/test_hosted.py -v
```

### Run the full aggregate quality gate

```bash
task test:all
```

Runs in order: `test:unit` → `test:e2e` → `test:python-agents` → `test:python-deployer` → `test:hosted`. This is the recommended pre-push gate.

### Individual suite shortcuts

| Command | Suite |
|---|---|
| `task test:domain` | Domain unit (Category A) |
| `task test:application` | Application unit (Category B) |
| `task test:api` | API contracts (Category C) |
| `task test:webui` | WebUI token handler (Category D) |
| `task test:infrastructure` | EF persistence (Category E) |
| `task test:e2e` | E2E lifecycle (Category E2E) |
| `task test:python-agents` | Python agent unit (Category P) |
| `task test:hosted` | Python hosted contracts (Category PH) |
| `task test:all` | All of the above |

---

## CI mapping

`.github/workflows/ci.yml` runs on every pull request and every push to `main`.

| CI job name | Coverage | Blocks deploy? |
|---|---|---|
| `.NET build & test` | Build, format check, Categories A–E | Yes — via workflow_run |
| `.NET E2E tests` | Real API/UI, application services, and EF repositories on SQLite | Yes |
| `Python agent syntax` | `compileall` for `src/agents/python` | Yes |
| `Python agent unit tests` | Category P | Yes |
| `Python hosted-agent tests` | Category PH | Yes |
| `Python deployer unit tests` | Deployer tests | Yes |
| `Terraform validate (infrastructure)` | fmt + validate | Yes |
| `Terraform validate (apps)` | fmt + validate | Yes |
| `Build container images` | All 7 Dockerfiles | Yes |

### Duplication avoidance

The main API job uses `--filter Category!=E2E`; the dedicated E2E job uses `--filter Category=E2E` and fails if `WorkflowE2eTests` are not discovered. Python agent and Hosted Agent tests likewise run in separate jobs.

### Deploy gate

`deploy-production.yml` is triggered by `workflow_run: [CI]` with `types: [completed]`. The first job condition is:

```yaml
if: >-
  github.event_name == 'workflow_dispatch' ||
  (github.event.workflow_run.conclusion == 'success' &&
   github.event.workflow_run.event == 'push' &&
   github.event.workflow_run.head_branch == 'main')
```

`workflow_run.conclusion` is `success` only when **all** CI jobs pass. Adding any new job to `ci.yml` automatically strengthens the gate — no change to `deploy-production.yml` is needed.

---

## Python working directory

All Python agent tests **must** run from `src/agents/python/`:

```bash
cd src/agents/python
python -m pytest tests -v
```

The `app` package imports use relative paths. Running `pytest` from the repo root resolves modules incorrectly and produces `ModuleNotFoundError`.

---

## Adding new tests

### .NET (E2E)

Decorate tests that require the full in-process stack with:

```csharp
[Trait("Category", "E2E")]
```

These tests will automatically be picked up by `task test:e2e` and the `dotnet-e2e` CI job.

### Python (hosted)

Add test functions to `src/agents/python/tests/test_hosted.py`. Once that file exists the `python-hosted-tests` CI job and `task test:hosted` will run them without any further pipeline changes.
