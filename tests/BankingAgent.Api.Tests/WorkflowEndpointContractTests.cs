using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using Moq;
using Xunit;

namespace BankingAgent.Api.Tests;

/// <summary>
/// Contract tests for workflow endpoint behavior:
/// - POST /api/v1/workflows: 202 Accepted + Location header + Draft status body
/// - GET returns workflow state including events, support case, and evidence
/// - GET missing ID returns 404
/// - Invalid transitions: 409 when approving non-WaitingForApproval workflow
/// - Idempotent approval: same decision returns 200 without error
/// - Conflicting decision: 409 when different decision already recorded
/// - Optimistic concurrency conflict: 409 via StaleVersionException
///
/// All tests run against TestOrchestratorHost with a valid Workflow.Invoke bearer token.
/// </summary>
public sealed class WorkflowEndpointContractTests : IDisposable
{
    private readonly Mock<IWorkflowService> _workflowServiceMock = new(MockBehavior.Loose);
    private readonly TestOrchestratorHost _testHost;
    private readonly HttpClient _client;

    private static readonly JsonSerializerOptions JsonOpts = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public WorkflowEndpointContractTests()
    {
        _testHost = new TestOrchestratorHost(_workflowServiceMock);
        _client = _testHost.CreateClient();

        var token = _testHost.BuildBearerToken();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
    }

    // ──────────────────────────────────────────────────────────────────
    // GET /api/v1/workflows/{id} — not found
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkflow_UnknownId_Returns404()
    {
        var workflowId = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.GetAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowState?)null);

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // GET /api/v1/workflows/{id} — durable retrieval with events
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkflow_ExistingId_Returns200WithWorkflowAndEvents()
    {
        var workflowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var workflow = new WorkflowState(
            workflowId,
            Guid.NewGuid().ToString("N"),
            "Why is this transaction pending?",
            WorkflowStatus.Completed,
            "transaction_explanation",
            RequiresApproval: false,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: t0,
            UpdatedAt: t0.AddSeconds(2),
            Events: [
                new("workflow.started", "Workflow started", t0, "system"),
                new("workflow.route_selected", "Route selected", t0.AddSeconds(1), "system"),
                new("workflow.completed", "Completed", t0.AddSeconds(2), "system"),
            ],
            Version: 3);

        _workflowServiceMock
            .Setup(s => s.GetAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _workflowServiceMock
            .Setup(s => s.GetSupportCaseAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupportCase?)null);

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.Equal(workflowId.ToString(), root.GetProperty("workflowId").GetString());
        Assert.Equal("Completed", root.GetProperty("status").GetString());
        Assert.Equal(3, root.GetProperty("events").GetArrayLength());
    }

    [Fact]
    public async Task GetWorkflow_ResponseEventsAreOrderedChronologically()
    {
        var workflowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var workflow = new WorkflowState(
            workflowId, Guid.NewGuid().ToString("N"), "msg",
            WorkflowStatus.Completed, "intent", false, null, null,
            t0, t0.AddSeconds(5),
            Events: [
                new("workflow.started", "Started", t0, "system"),
                new("workflow.route_selected", "Route", t0.AddSeconds(1), "system"),
                new("workflow.completed", "Done", t0.AddSeconds(5), "system"),
            ]);

        _workflowServiceMock
            .Setup(s => s.GetAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _workflowServiceMock
            .Setup(s => s.GetSupportCaseAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupportCase?)null);

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var events = doc.RootElement.GetProperty("events").EnumerateArray().ToList();

        for (int i = 1; i < events.Count; i++)
        {
            var prev = events[i - 1].GetProperty("timestamp").GetDateTimeOffset();
            var curr = events[i].GetProperty("timestamp").GetDateTimeOffset();
            Assert.True(curr >= prev,
                $"Event at index {i} ({curr}) is earlier than event at index {i - 1} ({prev})");
        }
    }

    [Fact]
    public async Task GetWorkflow_DraftStatus_ReturnsExpectedFields()
    {
        var workflowId = Guid.NewGuid();
        var traceId = Guid.NewGuid().ToString("N");
        var t0 = DateTimeOffset.UtcNow;
        var workflow = new WorkflowState(
            workflowId, traceId, "Explain this charge.",
            WorkflowStatus.Draft, null, false, null, null,
            t0, t0,
            Events: [new("workflow.started", "Workflow started", t0, "system")],
            Version: 0);

        _workflowServiceMock
            .Setup(s => s.GetAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _workflowServiceMock
            .Setup(s => s.GetSupportCaseAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupportCase?)null);

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        var root = doc.RootElement;

        Assert.Equal("Draft", root.GetProperty("status").GetString());
        Assert.Equal(traceId, root.GetProperty("traceId").GetString());
        Assert.Equal(0, root.GetProperty("version").GetInt64());
    }

    [Fact]
    public async Task GetWorkflow_WaitingForApproval_SupportCaseIsNull()
    {
        var workflowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var workflow = new WorkflowState(
            workflowId, Guid.NewGuid().ToString("N"), "Dispute this charge.",
            WorkflowStatus.WaitingForApproval, "dispute", true, null, null,
            t0, t0, Events: [], Version: 2);

        _workflowServiceMock
            .Setup(s => s.GetAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);
        _workflowServiceMock
            .Setup(s => s.GetSupportCaseAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((SupportCase?)null);

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        Assert.Equal("WaitingForApproval", doc.RootElement.GetProperty("status").GetString());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("supportCase").ValueKind);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST approval — invalid state → 409
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_WorkflowNotAwaitingApproval_Returns409()
    {
        var workflowId = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.ApproveAsync(workflowId, "approve", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidTransitionException(
                workflowId, WorkflowStatus.Completed, WorkflowStatus.WaitingForApproval.ToString()));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "state conflict probe" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST approval — not found → 404
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_UnknownWorkflowId_Returns404()
    {
        var workflowId = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.ApproveAsync(workflowId, "approve", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new WorkflowNotFoundException(workflowId));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "not-found probe" });

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST approval — conflicting decision → 409
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_ConflictingDecision_Returns409()
    {
        var workflowId = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.ApproveAsync(workflowId, "reject", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new ConflictingDecisionException(workflowId, "approve", "reject"));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "reject", reason = "conflicting decision probe" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST approval — idempotent (same decision already recorded) → 200
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_SameDecisionIdempotent_Returns200()
    {
        var workflowId = Guid.NewGuid();
        var existingWorkflow = new WorkflowState(
            workflowId, Guid.NewGuid().ToString("N"), "Dispute this charge.",
            WorkflowStatus.Completed, "dispute", true, "approve", "already approved",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        _workflowServiceMock
            .Setup(s => s.ApproveAsync(workflowId, "approve", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(existingWorkflow);

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "idempotent probe" });

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST approval — optimistic concurrency conflict → 409
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_StaleVersionException_Returns409()
    {
        var workflowId = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.ApproveAsync(workflowId, "approve", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StaleVersionException(workflowId, expectedVersion: 1, actualVersion: 2));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "stale version probe" });

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST approval — idempotent retry returns identical body
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_IdempotentRetry_ReturnsSameBodyBothTimes()
    {
        var workflowId = Guid.NewGuid();
        var completedWorkflow = new WorkflowState(
            workflowId, "t1", "Dispute this charge.",
            WorkflowStatus.Completed, "dispute", true, "approve", "approved",
            DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []);

        _workflowServiceMock
            .Setup(s => s.ApproveAsync(workflowId, "approve", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(completedWorkflow);

        var first = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "retry probe" });
        var second = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "retry probe" });

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.Equal(HttpStatusCode.OK, second.StatusCode);

        var body1 = await first.Content.ReadAsStringAsync();
        var body2 = await second.Content.ReadAsStringAsync();
        Assert.Equal(body1, body2);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST /api/v1/workflows — 202 Accepted with Location header
    // (Contract: post returns immediately with Draft status; execution
    //  is claimed asynchronously by the recovery worker)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_ValidRequest_Returns202AcceptedWithLocationHeader()
    {
        var expectedId = Guid.NewGuid();
        var traceId = Guid.NewGuid().ToString("N");
        _workflowServiceMock
            .Setup(s => s.StartAsync("Why is this transaction pending?", It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowState(
                expectedId, traceId, "Why is this transaction pending?",
                WorkflowStatus.Draft, null, false, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Why is this transaction pending?" });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);

        // Location header must be present and point to GET endpoint
        Assert.NotNull(response.Headers.Location);
        Assert.Contains($"/api/v1/workflows/{expectedId}", response.Headers.Location.ToString());

        var content = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(content);
        Assert.Equal(expectedId.ToString(), doc.RootElement.GetProperty("workflowId").GetString());
        Assert.Equal(traceId, doc.RootElement.GetProperty("traceId").GetString());
        Assert.Equal("Draft", doc.RootElement.GetProperty("status").GetString());
    }

    [Fact]
    public async Task PostWorkflow_ResponseBody_ContainsDraftStatusAndNonNullMessage()
    {
        var id = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowState(
                id, "trace-abc", "Test request.",
                WorkflowStatus.Draft, null, false, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Test request." });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(body);
        Assert.Equal("Draft", doc.RootElement.GetProperty("status").GetString());
        var message = doc.RootElement.GetProperty("message").GetString();
        Assert.False(string.IsNullOrWhiteSpace(message), "202 response must include a non-empty message.");
    }

    [Fact]
    public async Task PostWorkflow_ServiceIsCalled_OnceOnly()
    {
        var id = Guid.NewGuid();
        _workflowServiceMock
            .Setup(s => s.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowState(
                id, "t", "msg",
                WorkflowStatus.Draft, null, false, null, null,
                DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, []));

        await _client.PostAsJsonAsync("/api/v1/workflows", new { userMessage = "msg" });

        // StartAsync is called exactly once; planner/specialist execution is deferred
        _workflowServiceMock.Verify(
            s => s.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task PostWorkflow_DemoScenario_Returns202AndCallsStartDemoAsync()
    {
        var expectedId = Guid.NewGuid();
        _workflowServiceMock
            .Setup(service => service.StartDemoAsync(
                "hosted-agent-failure",
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new WorkflowState(
                expectedId,
                "demo-trace",
                "Synthetic demo request",
                WorkflowStatus.Draft,
                null,
                false,
                null,
                null,
                DateTimeOffset.UtcNow,
                DateTimeOffset.UtcNow,
                []));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new
            {
                userMessage = "Synthetic demo request",
                demoScenario = "hosted-agent-failure"
            });

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        _workflowServiceMock.Verify(
            service => service.StartDemoAsync(
                "hosted-agent-failure",
                It.IsAny<CancellationToken>()),
            Times.Once);
        _workflowServiceMock.Verify(
            service => service.StartAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────────────────────────

    public void Dispose()
    {
        _client.Dispose();
        _testHost.Dispose();
    }
}
