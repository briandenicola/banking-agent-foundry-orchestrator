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
/// Contract tests for orchestrator JWT authentication behavior.
/// Covers acceptance criteria from aria-p0-design-review.md §Authentication (Lumen):
///
///   ✓ Orchestrator rejects anonymous /api/v1/... requests with 401
///   ✓ Orchestrator accepts valid JWT with Workflow.Invoke role
///   ✓ /health remains accessible without authentication
///   ✓ Wrong role returns 403 Forbidden
///
/// These tests run against TestOrchestratorHost — an in-process host that mirrors
/// the expected post-Lumen auth configuration without modifying production files.
/// </summary>
public sealed class AuthenticationContractTests : IDisposable
{
    private readonly Mock<IWorkflowService> _workflowServiceMock = new(MockBehavior.Loose);
    private readonly TestOrchestratorHost _testHost;
    private readonly HttpClient _client;

    public AuthenticationContractTests()
    {
        _testHost = new TestOrchestratorHost(_workflowServiceMock);
        _client = _testHost.CreateClient();
    }

    // ──────────────────────────────────────────────────────────────────
    // /health — must be anonymous
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Health_Anonymous_Returns200()
    {
        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    [Fact]
    public async Task Health_DoesNotRequireBearer_Returns200WithoutHeader()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.GetAsync("/health");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // POST /api/v1/workflows — anonymous must return 401
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_Anonymous_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Why is this charge pending?" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("authentication_required", await ReadProblemCodeAsync(response));
    }

    // ──────────────────────────────────────────────────────────────────
    // POST /api/v1/workflows/{id}/approval — anonymous must return 401
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostApproval_Anonymous_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var fakeId = Guid.NewGuid();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{fakeId}/approval",
            new { decision = "approve", reason = "auth baseline probe" });

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // Wrong role — must return 403 Forbidden
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_WrongRole_Returns403()
    {
        var token = _testHost.BuildBearerToken(role: "SomeOtherRole");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Wrong role test" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
        Assert.Equal("access_forbidden", await ReadProblemCodeAsync(response));
    }

    [Fact]
    public async Task PostApproval_WrongRole_Returns403()
    {
        var token = _testHost.BuildBearerToken(role: "Reader");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        var fakeId = Guid.NewGuid();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{fakeId}/approval",
            new { decision = "approve", reason = "wrong role probe" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    [Fact]
    public async Task PostApproval_NoRoleClaim_Returns403()
    {
        var token = _testHost.BuildBearerToken(role: null);
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        var fakeId = Guid.NewGuid();

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{fakeId}/approval",
            new { decision = "approve", reason = "no role probe" });

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // Valid token with Workflow.Invoke role — reaches business logic
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task PostWorkflow_ValidTokenWithWorkflowInvokeRole_PassesAuthentication()
    {
        // Auth passes; service mock throws to isolate auth from logic
        _workflowServiceMock
            .Setup(s => s.StartAsync(It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("sentinel"));

        var token = _testHost.BuildBearerToken();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Valid token test" });

        // A business-logic response proves auth passed — not 401/403.
        Assert.NotEqual(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.NotEqual(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // GET /api/v1/workflows/{id} — anonymous must return 401
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetWorkflow_Anonymous_Returns401()
    {
        _client.DefaultRequestHeaders.Authorization = null;
        var workflowId = Guid.NewGuid();

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
    }

    [Fact]
    public async Task GetWorkflow_WrongRole_Returns403()
    {
        var token = _testHost.BuildBearerToken(role: "SomeOtherRole");
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        var workflowId = Guid.NewGuid();

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");

        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode);
    }

    // ──────────────────────────────────────────────────────────────────
    // Cleanup
    // ──────────────────────────────────────────────────────────────────

    private static async Task<string?> ReadProblemCodeAsync(HttpResponseMessage response)
    {
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        return document.RootElement.GetProperty("code").GetString();
    }

    public void Dispose()
    {
        _client.Dispose();
        _testHost.Dispose();
    }
}
