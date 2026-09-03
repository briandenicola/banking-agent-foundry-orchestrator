using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using webui.Pages;
using Xunit;

namespace BankingAgent.Api.Tests;

[Trait("Category", "E2E")]
public sealed class WorkflowE2eTests : IDisposable
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"banking-agent-e2e-{Guid.NewGuid():N}.db");
    private readonly BankingAgentDbContext _context;
    private readonly TestOrchestratorHost _host;
    private readonly HttpClient _client;

    private static readonly IReadOnlySet<string> PollingTerminalStatuses =
        new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            "Completed", "Failed", "Rejected", "WaitingForApproval"
        };

    public WorkflowE2eTests()
    {
        _context = CreateContext();
        _context.Database.EnsureCreated();
        var workflowRepository = new EfWorkflowRepository(_context);
        var workflowService = new WorkflowService(
            new FullRoutingMcpClient(),
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
    // Full dispute lifecycle: POST 202 → evidence → approve → Completed
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task DisputeWorkflow_WhenApproved_PersistsSupportCaseAndEvidence()
    {
        var workflowId = await PostAndRecoverAsync(
            "Dispute demo transaction DEMO-TXN-1001 at Northwind Market.",
            "WaitingForApproval");

        using var evidence = new MultipartFormDataContent();
        evidence.Add(
            new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "files",
            "receipt.png");
        var evidenceResponse = await _client.PostAsync(
            $"/api/v1/workflows/{workflowId}/evidence",
            evidence);
        Assert.True(
            evidenceResponse.IsSuccessStatusCode,
            await evidenceResponse.Content.ReadAsStringAsync());

        var approvalResponse = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "E2E approval" });
        Assert.Equal(HttpStatusCode.OK, approvalResponse.StatusCode);

        var detail = await GetWorkflowAsync(workflowId);
        Assert.Equal("Completed", detail.GetProperty("status").GetString());
        Assert.Equal("approve", detail.GetProperty("approvalDecision").GetString());
        Assert.NotEqual(JsonValueKind.Null, detail.GetProperty("supportCase").ValueKind);
        Assert.Single(detail.GetProperty("evidence").EnumerateArray());
        Assert.Equal(
            1,
            await _context.SupportCases.CountAsync(item => item.WorkflowId == workflowId));
        Assert.Equal(
            1,
            await _context.WorkflowEvidence.CountAsync(item => item.WorkflowId == workflowId));
    }

    [Fact]
    public async Task DisputeWorkflow_WhenRejected_PersistsNoSupportCase()
    {
        var workflowId = await PostAndRecoverAsync(
            "Dispute demo transaction DEMO-TXN-1001 at Northwind Market.",
            "WaitingForApproval");

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "reject", reason = "Transaction was recognized." });
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var detail = await GetWorkflowAsync(workflowId);
        Assert.Equal("Rejected", detail.GetProperty("status").GetString());
        Assert.Equal("reject", detail.GetProperty("approvalDecision").GetString());
        Assert.Equal(JsonValueKind.Null, detail.GetProperty("supportCase").ValueKind);
        Assert.Equal(
            0,
            await _context.SupportCases.CountAsync(item => item.WorkflowId == workflowId));
    }

    // ──────────────────────────────────────────────────────────────────
    // Idempotent approval: two identical POST approvals, one decision row
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApprovalRetry_ReturnsIdenticalResponseAndNoDuplicateAction()
    {
        var workflowId = await PostAndRecoverAsync(
            "Dispute demo transaction DEMO-TXN-1001 at Northwind Market.",
            "WaitingForApproval");
        var payload = new { decision = "approve", reason = "Approved after review." };

        var first = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            payload);
        var second = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            payload);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);
        Assert.Equal(
            await first.Content.ReadAsStringAsync(),
            await second.Content.ReadAsStringAsync());
        Assert.Equal(
            1,
            await _context.ActionExecutions.CountAsync(item => item.WorkflowId == workflowId));
        Assert.Equal(
            1,
            await _context.ApprovalDecisions.CountAsync(item => item.WorkflowId == workflowId));
    }

    // ──────────────────────────────────────────────────────────────────
    // Evidence attached before specialist processing observes it
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Evidence_AttachedAfterPost_BeforeRecovery_IsPersistedDurably()
    {
        // POST → 202 (workflow is Draft, specialist has not run yet)
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Dispute demo transaction DEMO-TXN-1001 at Northwind Market." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        // Attach evidence while still Draft — before specialist can run
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "files", "receipt.png");
        var evidenceResponse = await _client.PostAsync(
            $"/api/v1/workflows/{workflowId}/evidence", form);
        Assert.True(evidenceResponse.IsSuccessStatusCode,
            await evidenceResponse.Content.ReadAsStringAsync());

        // Evidence is stored immediately; specialist has not observed it yet
        var dbCount = await _context.WorkflowEvidence.CountAsync(
            e => e.WorkflowId == workflowId);
        Assert.Equal(1, dbCount);
    }

    [Fact]
    public async Task Evidence_UploadedBeforeExecution_IsVisibleAfterRecovery()
    {
        // POST (draft)
        var postResponse = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Dispute demo transaction DEMO-TXN-1001 at Northwind Market." });
        Assert.Equal(HttpStatusCode.Accepted, postResponse.StatusCode);
        var postBody = await postResponse.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(postBody.GetProperty("workflowId").GetString()!);

        // Upload evidence before execution
        using var form = new MultipartFormDataContent();
        form.Add(new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]),
            "files", "statement.png");
        await _client.PostAsync($"/api/v1/workflows/{workflowId}/evidence", form);

        // Simulate background execution
        await RunRecoveryAsync(workflowId, new FullRoutingMcpClient());

        // Evidence persists after recovery; GET returns it
        var detail = await GetWorkflowAsync(workflowId);
        Assert.Equal(1, detail.GetProperty("evidence").GetArrayLength());
    }

    // ──────────────────────────────────────────────────────────────────
    // Web UI submission: post + redirect + load via IndexModel
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task WebUiSubmission_RunsThroughApiAndDisplaysPersistedWorkflow()
    {
        // The IndexModel submits to the API and redirects.
        // Under the 202 contract the redirect goes to the polling status page.
        var page = CreateIndexModel();
        page.Input.UserMessage =
            "Why is demo transaction DEMO-TXN-1002 at Metro Transit pending?";

        var result = Assert.IsType<RedirectToPageResult>(await page.OnPostAsync());
        var workflowId = Assert.IsType<Guid>(result.RouteValues!["workflowId"]);

        // Simulate background recovery so the workflow reaches a terminal state
        await RunRecoveryAsync(workflowId, new FullRoutingMcpClient());

        var statusPage = CreateIndexModel();
        await statusPage.OnGetAsync(workflowId);

        Assert.True(
            statusPage.Workflow is not null,
            statusPage.ErrorMessage ?? "Workflow was not loaded.");
        Assert.Equal(workflowId, statusPage.Workflow.WorkflowId);
        Assert.True(
            PollingTerminalStatuses.Contains(statusPage.Workflow.Status),
            $"Expected terminal status but got: {statusPage.Workflow.Status}");
        Assert.NotNull(await new EfWorkflowRepository(_context).GetAsync(workflowId));
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    /// <summary>
    /// POST the workflow (expects 202 Accepted under new contract),
    /// then explicitly trigger RecoverAsync to simulate the background worker,
    /// and verify the workflow reaches <paramref name="expectedStatus"/>.
    /// </summary>
    private async Task<Guid> PostAndRecoverAsync(string userMessage, string expectedStatus)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage });
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        var workflowId = Guid.Parse(body.GetProperty("workflowId").GetString()!);

        // Verify Location header points to the workflow
        Assert.NotNull(response.Headers.Location);
        Assert.Contains(
            workflowId.ToString(),
            response.Headers.Location.ToString(),
            StringComparison.OrdinalIgnoreCase);

        // Run background execution
        await RunRecoveryAsync(workflowId, new FullRoutingMcpClient());

        // Verify the workflow has reached the expected status
        var detail = await GetWorkflowAsync(workflowId);
        Assert.Equal(expectedStatus, detail.GetProperty("status").GetString());

        return workflowId;
    }

    private async Task RunRecoveryAsync(Guid workflowId, IMcpClient mcpClient)
    {
        await using var rcCtx = CreateContext();
        var service = new WorkflowService(
            mcpClient,
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(rcCtx),
            new EfWorkflowActionRepository(rcCtx));
        await service.RecoverAsync(workflowId);
    }

    private async Task<JsonElement> GetWorkflowAsync(Guid workflowId)
    {
        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.True(response.IsSuccessStatusCode, await response.Content.ReadAsStringAsync());
        return await response.Content.ReadFromJsonAsync<JsonElement>();
    }

    private IndexModel CreateIndexModel()
    {
        var model = new IndexModel(
            new TestHttpClientFactory(_client),
            NullLogger<IndexModel>.Instance,
            new StubSignedInCustomerAccessor())
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        model.TempData = new TempDataDictionary(
            model.HttpContext,
            new TestTempDataProvider());
        return model;
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

    private sealed class TestHttpClientFactory(HttpClient client) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => client;
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(
            HttpContext context,
            IDictionary<string, object> values)
        {
        }
    }

    private sealed class FullRoutingMcpClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            var userMessage = parameters.TryGetValue("user_message", out var message)
                ? message?.ToString() ?? string.Empty
                : string.Empty;
            var route = WorkflowRoutingPolicy.Decide(userMessage);
            var agent = toolName == "workflow.plan" ? "workflow-planning" : route.Agent;
            var body = JsonSerializer.Serialize(new
            {
                agent,
                status = "ok",
                intent = route.Agent,
                summary = $"Synthetic E2E result from {toolName}.",
                requires_approval = route.RequiresApproval,
                selected_agent = toolName == "workflow.plan" ? route.Agent : null,
                evidence = Array.Empty<string>()
            });
            return Task.FromResult(new McpToolResult(
                toolName,
                "ok",
                $"Synthetic E2E result from {toolName}.",
                new Dictionary<string, object?> { ["response_body"] = body }));
        }
    }
}
