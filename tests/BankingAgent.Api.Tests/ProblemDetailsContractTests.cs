using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using Moq;
using Xunit;

namespace BankingAgent.Api.Tests;

public sealed class ProblemDetailsContractTests : IDisposable
{
    private readonly Mock<IWorkflowService> _workflowService = new(MockBehavior.Loose);
    private readonly TestOrchestratorHost _host;
    private readonly HttpClient _client;

    public ProblemDetailsContractTests()
    {
        _host = new TestOrchestratorHost(_workflowService);
        _client = _host.CreateClient();
        _client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", _host.BuildBearerToken());
    }

    [Fact]
    public async Task PostWorkflow_BlankMessage_ReturnsActionableValidationProblem()
    {
        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = " " });

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "validation_failed");
        Assert.True(problem.GetProperty("errors").TryGetProperty("userMessage", out _));
    }

    [Fact]
    public async Task PostApproval_InvalidDecisionAndReason_ReturnsValidationErrors()
    {
        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{Guid.NewGuid()}/approval",
            new { decision = "maybe", reason = "" });

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.BadRequest,
            "validation_failed");
        var errors = problem.GetProperty("errors");
        Assert.True(errors.TryGetProperty("decision", out _));
        Assert.True(errors.TryGetProperty("reason", out _));
    }

    [Fact]
    public async Task GetWorkflow_NotFound_ReturnsStableProblemCode()
    {
        var workflowId = Guid.NewGuid();
        _workflowService
            .Setup(service => service.GetAsync(workflowId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowState?)null);

        var response = await _client.GetAsync($"/api/v1/workflows/{workflowId}");

        await AssertProblemAsync(response, HttpStatusCode.NotFound, "workflow_not_found");
    }

    [Fact]
    public async Task PostApproval_Conflict_ReturnsStableProblemCode()
    {
        var workflowId = Guid.NewGuid();
        _workflowService
            .Setup(service => service.ApproveAsync(
                workflowId,
                "approve",
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StaleVersionException(workflowId, 1, 2));

        var response = await _client.PostAsJsonAsync(
            $"/api/v1/workflows/{workflowId}/approval",
            new { decision = "approve", reason = "Approved" });

        await AssertProblemAsync(response, HttpStatusCode.Conflict, "workflow_conflict");
    }

    [Fact]
    public async Task PostWorkflow_DependencyFailure_Returns502WithoutInternalMessage()
    {
        _workflowService
            .Setup(service => service.StartAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("secret downstream endpoint"));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain this transaction." });

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.BadGateway,
            "dependency_unavailable");
        Assert.DoesNotContain("secret downstream endpoint", problem.GetRawText());
    }

    [Fact]
    public async Task PostWorkflow_DependencyTimeout_Returns504()
    {
        _workflowService
            .Setup(service => service.StartAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new TimeoutException("Foundry timeout details"));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain this transaction." });

        await AssertProblemAsync(response, HttpStatusCode.GatewayTimeout, "dependency_timeout");
    }

    [Fact]
    public async Task PostWorkflow_UnexpectedFailure_ReturnsGeneric500WithoutStackTrace()
    {
        _workflowService
            .Setup(service => service.StartAsync(
                It.IsAny<string>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new InvalidOperationException("database password leaked"));

        var response = await _client.PostAsJsonAsync(
            "/api/v1/workflows",
            new { userMessage = "Explain this transaction." });

        var problem = await AssertProblemAsync(
            response,
            HttpStatusCode.InternalServerError,
            "internal_error");
        Assert.DoesNotContain("database password leaked", problem.GetRawText());
        Assert.DoesNotContain("stack", problem.GetRawText(), StringComparison.OrdinalIgnoreCase);
    }

    private static async Task<JsonElement> AssertProblemAsync(
        HttpResponseMessage response,
        HttpStatusCode expectedStatus,
        string expectedCode)
    {
        Assert.Equal(expectedStatus, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
        var root = document.RootElement.Clone();
        Assert.Equal((int)expectedStatus, root.GetProperty("status").GetInt32());
        Assert.Equal(expectedCode, root.GetProperty("code").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("traceId").GetString()));
        return root;
    }

    public void Dispose()
    {
        _client.Dispose();
        _host.Dispose();
    }
}
