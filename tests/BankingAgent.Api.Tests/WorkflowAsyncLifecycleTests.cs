using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BankingAgent.Api.Tests;

/// <summary>
/// Async lifecycle contract tests: validates the full 202 → Draft → polling → terminal
/// and failure paths against a real in-process stack (SQLite + deterministic MCP doubles).
///
/// Terminal polling statuses: Completed, Failed, Rejected, WaitingForApproval.
/// These tests will fail until Theo lands the 202/async endpoint change; they document
/// the accepted Aria ADR contract and serve as the quality gate for that change.
/// </summary>
[Trait("Category", "E2E")]
public sealed class WorkflowAsyncLifecycleTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"banking-agent-async-{Guid.NewGuid():N}.db");
    private readonly BankingAgentDbContext _context;
    private readonly TestOrchestratorHost _host;
    private readonly HttpClient _client;

    private static readonly IReadOnlySet<string> PollingTerminalStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Completed", "Failed", "Rejected", "WaitingForApproval"
        };

    public WorkflowAsyncLifecycleTests()
    {
        _context = CreateContext();
        _context.Database.EnsureCreated();
        var workflowRepository = new EfWorkflowRepository(_context);
        var workflowService = new WorkflowService(
            new DeterministicTransactionExplanationClient(),
            NullLogger<WorkflowService>.Instance,
            workflowRepository,
            new EfWorkflowActionRepository(_context));
        var evidenceService = new WorkflowEvidenceService(
            workflowRepository,
            new EfWorkflowEvidenceRepository(_context));

        _host = new TestOrchestratorHost(workflowService, evidenceService);
        _client = _host.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.BuildBearerToken());
    }

    // ──────────────────────────────────────────────────────────────────
    // POST returns 202 with Location and Draft status immediately
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_Returns202WithLocationAndDraftStatus()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Why is demo transaction DEMO-TXN-1002 at Metro Transit pending?" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.NotNull(response.Headers.Location);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Draft", body.GetProperty("status").GetString());
        Assert.NotEmpty(body.GetProperty("workflowId").GetString()!);
        Assert.NotEmpty(body.GetProperty("traceId").GetString()!);
        Assert.NotEmpty(body.GetProperty("message").GetString()!);
    }

    [Fact]
    public async Task PostWorkflow_LocationHeader_PointsToGetEndpoint()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain DEMO-TXN-1001." });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = body.GetProperty("workflowId").GetString();
        var location = response.Headers.Location?.ToString();

        Assert.NotNull(location);
        Assert.Contains($"/api/v1/workflows/{workflowId}", location,
            StringComparison.OrdinalIgnoreCase);
    }

    // ──────────────────────────────────────────────────────────────────
    // Draft workflow is immediately visible via GET
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_DraftWorkflow_IsVisibleViaGetImmediately()
    {
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain DEMO-TXN-1001." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);

        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = postBody.GetProperty("workflowId").GetString()!;

        var getResponse = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var detail = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal(workflowId, detail.GetProperty("workflowId").GetString());
        // status is Draft or already processed — either is valid
        Assert.NotEmpty(detail.GetProperty("status").GetString()!);
    }

    // ──────────────────────────────────────────────────────────────────
    // Polling lifecycle: poll until terminal status
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollWorkflow_AfterRecovery_ReachesTerminalStatus()
    {
        // POST gets a Draft workflow
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain DEMO-TXN-1002 at Metro Transit." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        // Simulate background execution: directly call RecoverAsync (as the worker would)
        await using var recoveryContext = CreateContext();
        var recoveryService = new WorkflowService(
            new DeterministicTransactionExplanationClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(recoveryContext),
            new EfWorkflowActionRepository(recoveryContext));

        await recoveryService.RecoverAsync(workflowId);

        // Poll via GET — must reach terminal status
        var terminal = await PollUntilTerminalAsync(workflowId, maxAttempts: 5);
        Assert.True(
            PollingTerminalStatuses.Contains(terminal.GetProperty("status").GetString()!),
            $"Expected terminal status but got: {terminal.GetProperty("status").GetString()}");
    }

    [Fact]
    public async Task PollWorkflow_GetsUpdatedEventsAfterExecution()
    {
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain DEMO-TXN-1002 at Metro Transit." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        var draftDetail = await GetDetailAsync(workflowId);
        var draftEventCount = draftDetail.GetProperty("events").GetArrayLength();

        // Simulate background execution
        await using var recoveryContext = CreateContext();
        var svc = new WorkflowService(
            new DeterministicTransactionExplanationClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(recoveryContext),
            new EfWorkflowActionRepository(recoveryContext));
        await svc.RecoverAsync(workflowId);

        var terminalDetail = await GetDetailAsync(workflowId);
        var terminalEventCount = terminalDetail.GetProperty("events").GetArrayLength();

        Assert.True(
            terminalEventCount > draftEventCount,
            $"Expected more events after execution ({terminalEventCount} vs {draftEventCount}).");
    }

    // ──────────────────────────────────────────────────────────────────
    // Planner/specialist failure path: workflow goes to Failed
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_PlannerFailure_RecoveryPersistsFailedStatus()
    {
        // StartAsync persists Draft; planner failure happens in RecoverAsync
        var failingService = new WorkflowService(
            new AlwaysFailingMcpClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(_context),
            new EfWorkflowActionRepository(_context));

        var draft = await failingService.StartAsync("Explain a pending charge.");
        Assert.Equal(WorkflowStatus.Draft, draft.Status);

        var result = await failingService.RecoverAsync(draft.Id);

        Assert.Equal(WorkflowStatus.Failed, result.Status);
        var persisted = await new EfWorkflowRepository(_context).GetAsync(draft.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowStatus.Failed, persisted.Status);
        Assert.Contains(persisted.Events, e => e.Type == "workflow.failed");
    }

    [Fact]
    public async Task PostWorkflow_PlannerFailure_GetViaApiReturnsFailedStatus()
    {
        // Use a workflow pre-seeded as Failed to test GET rendering
        var workflowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var failedWorkflow = new WorkflowState(
            workflowId, Guid.NewGuid().ToString("N"), "Failing request.",
            WorkflowStatus.Failed, null, false, null, null,
            t0, t0.AddSeconds(1),
            Events: [
                new("workflow.started", "Workflow started", t0, "system"),
                new("workflow.failed", "Agent invocation failed", t0.AddSeconds(1), "system"),
            ],
            Version: 1);
        await new EfWorkflowRepository(_context).AddAsync(failedWorkflow);

        var getResponse = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.Equal(HttpStatusCode.OK, getResponse.StatusCode);

        var detail = await getResponse.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("Failed", detail.GetProperty("status").GetString());
        Assert.Equal(2, detail.GetProperty("events").GetArrayLength());
    }

    // ──────────────────────────────────────────────────────────────────
    // WaitingForApproval: polling stops at approval gate
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PollWorkflow_WaitingForApproval_IsTerminalStatusForPoller()
    {
        var workflowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var waitingWorkflow = new WorkflowState(
            workflowId, Guid.NewGuid().ToString("N"), "Dispute DEMO-TXN-1001.",
            WorkflowStatus.WaitingForApproval, "dispute", true, null, null,
            t0, t0.AddSeconds(2),
            Events: [
                new("workflow.started", "Started", t0, "system"),
                new("workflow.waiting", "Waiting for approval", t0.AddSeconds(2), "system"),
            ],
            Version: 2);
        await new EfWorkflowRepository(_context).AddAsync(waitingWorkflow);

        var detail = await GetDetailAsync(workflowId);
        Assert.Equal("WaitingForApproval", detail.GetProperty("status").GetString());
        Assert.True(
            PollingTerminalStatuses.Contains(detail.GetProperty("status").GetString()!),
            "WaitingForApproval must be treated as a polling stop by the UI.");
    }

    // ──────────────────────────────────────────────────────────────────
    // Approval resume: workflow proceeds from WaitingForApproval
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveWorkflow_WaitingForApproval_ResumesToCompleted()
    {
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Dispute demo transaction DEMO-TXN-1001 at Northwind Market." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        // Drive to WaitingForApproval via recovery
        await using var rcCtx = CreateContext();
        await new WorkflowService(
            new DeterministicDisputeClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(rcCtx),
            new EfWorkflowActionRepository(rcCtx))
            .RecoverAsync(workflowId);

        var waiting = await GetDetailAsync(workflowId);
        Assert.Equal("WaitingForApproval", waiting.GetProperty("status").GetString());

        var approvalResponse = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "E2E async approval." });
        Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);

        var final = await GetDetailAsync(workflowId);
        Assert.Equal("Completed", final.GetProperty("status").GetString());
        Assert.Equal("approve", final.GetProperty("approvalDecision").GetString());
    }

    // ──────────────────────────────────────────────────────────────────
    // Evidence association before specialist execution
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Evidence_AttachedAfterPost_IsVisibleBeforeSpecialistProcessing()
    {
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Dispute demo transaction DEMO-TXN-1001 at Northwind Market." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        // Attach evidence while workflow is still Draft (before specialist execution)
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "files", "receipt.png");
        var evidenceResponse = await _client.PostAsync(
            $"/api/v1/workflows/{workflowId}/evidence", form);
        Assert.True(evidenceResponse.IsSuccessStatusCode,
            await evidenceResponse.Content.ReadAsStringAsync());

        // Evidence must be stored immediately (before any execution)
        var detail = await GetDetailAsync(workflowId);
        Assert.Equal(1, detail.GetProperty("evidence").GetArrayLength());
    }

    [Fact]
    public async Task Evidence_UploadedBeforeExecution_SurvivesRecovery()
    {
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Dispute DEMO-TXN-1001 at Northwind Market." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "files", "statement.png");
        await _client.PostAsync($"/api/v1/workflows/{workflowId}/evidence", form);

        // Simulate recovery (specialist execution)
        await using var rcCtx = CreateContext();
        await new WorkflowService(
            new DeterministicDisputeClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(rcCtx),
            new EfWorkflowActionRepository(rcCtx))
            .RecoverAsync(workflowId);

        // Evidence must persist after execution
        var detail = await GetDetailAsync(workflowId);
        Assert.Equal(1, detail.GetProperty("evidence").GetArrayLength());
        Assert.Equal("statement.png",
            detail.GetProperty("evidence")[0].GetProperty("fileName").GetString());
    }

    // ──────────────────────────────────────────────────────────────────
    // Support case and events are included in GET response
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkflow_AfterApproval_IncludesSupportCaseAndEvents()
    {
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Dispute demo transaction DEMO-TXN-1001 at Northwind Market." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        await using var rcCtx = CreateContext();
        await new WorkflowService(
            new DeterministicDisputeClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(rcCtx),
            new EfWorkflowActionRepository(rcCtx))
            .RecoverAsync(workflowId);

        await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "Support case probe." });

        var detail = await GetDetailAsync(workflowId);
        Assert.Equal("Completed", detail.GetProperty("status").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("supportCase").ValueKind);
        Assert.True(detail.GetProperty("events").GetArrayLength() > 0,
            "Completed approved workflow must have events in GET response.");
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private async Task<JsonElement> GetDetailAsync(Guid workflowId)
    {
        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private async Task<JsonElement> PollUntilTerminalAsync(Guid workflowId, int maxAttempts = 10)
    {
        for (var attempt = 0; attempt < maxAttempts; attempt++)
        {
            var detail = await GetDetailAsync(workflowId);
            var status = detail.GetProperty("status").GetString()!;
            if (PollingTerminalStatuses.Contains(status))
            {
                return detail;
            }

            await Task.Delay(50);
        }

        return await GetDetailAsync(workflowId);
    }

    private BankingAgentDbContext CreateContext()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        var options = new DbContextOptionsBuilder<BankingAgentDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new BankingAgentDbContext(options);
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
        _context.Dispose();
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }
    }

    // ──────────────────────────────────────────────────────────────────
    // Deterministic MCP doubles
    // ──────────────────────────────────────────────────────────────────

    private sealed class DeterministicTransactionExplanationClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            var agent = toolName == "workflow.plan" ? "workflow-planning" : "transaction-explanation";
            var body = JsonSerializer.Serialize(new
            {
                agent,
                status = "ok",
                intent = "transaction-explanation",
                summary = "Synthetic transaction explanation.",
                requires_approval = false,
                selected_agent = toolName == "workflow.plan" ? "transaction-explanation" : (object?)null,
                evidence = Array.Empty<string>()
            });
            return Task.FromResult(new McpToolResult(toolName, "ok", "Synthetic result.",
                new Dictionary<string, object?> { ["response_body"] = body }));
        }
    }

    private sealed class DeterministicDisputeClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            var agent = toolName == "workflow.plan" ? "workflow-planning" : "dispute-planning";
            var body = JsonSerializer.Serialize(new
            {
                agent,
                status = "ok",
                intent = "dispute-planning",
                summary = "Dispute workflow accepted for review.",
                requires_approval = true,
                selected_agent = toolName == "workflow.plan" ? "dispute-planning" : (object?)null,
                evidence = Array.Empty<string>()
            });
            return Task.FromResult(new McpToolResult(toolName, "ok", "Synthetic dispute result.",
                new Dictionary<string, object?> { ["response_body"] = body }));
        }
    }

    private sealed class AlwaysFailingMcpClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default) =>
            throw new HttpRequestException("Foundry unavailable (synthetic failure).");
    }
}
