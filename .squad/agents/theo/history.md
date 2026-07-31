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
