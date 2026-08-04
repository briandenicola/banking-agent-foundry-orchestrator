using System.Net;
using System.Text.Json.Nodes;
using BankingAgent.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class FoundryMcpClientReliabilityTests
{
    [Fact]
    public async Task InvokeAsync_TransientResponses_RetriesUntilSuccess()
    {
        var handler = new SequenceHandler(
            _ => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(Response(HttpStatusCode.TooManyRequests)),
            _ => Task.FromResult(Response(HttpStatusCode.OK, """{"status":"ok"}""")));
        var client = CreateClient(handler, maxAttempts: 3);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("ok", result.Status);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(3, result.Data["attempts"]);
    }

    [Fact]
    public async Task InvokeAsync_NonTransientResponse_DoesNotRetry()
    {
        var handler = new SequenceHandler(
            _ => Task.FromResult(Response(HttpStatusCode.BadRequest)));
        var client = CreateClient(handler, maxAttempts: 3);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, result.Data["attempts"]);
    }

    [Fact]
    public async Task InvokeAsync_TransportFailure_StopsAtBoundedAttemptCount()
    {
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException("synthetic transport failure"),
            _ => throw new HttpRequestException("synthetic transport failure"),
            _ => throw new HttpRequestException("synthetic transport failure"));
        var client = CreateClient(handler, maxAttempts: 3);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal("transport_error", result.Data["error_code"]);
        Assert.Equal(3, result.Data["attempts"]);
        Assert.DoesNotContain(
            "synthetic transport failure",
            string.Join(" ", result.Data.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_AttemptTimeout_StopsAtBoundedAttemptCount()
    {
        var handler = new SequenceHandler(
            token => Task.FromCanceled<HttpResponseMessage>(
                new CancellationToken(canceled: true)),
            token => Task.FromCanceled<HttpResponseMessage>(
                new CancellationToken(canceled: true)));
        var client = CreateClient(handler, maxAttempts: 2);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal("timeout", result.Data["error_code"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellation_IsNeverRetried()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new SequenceHandler(
            token => Task.FromCanceled<HttpResponseMessage>(token));
        var client = CreateClient(handler, maxAttempts: 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.InvokeAsync(
                "workflow.plan",
                Parameters(),
                cancellation.Token));

        Assert.Equal(1, handler.Attempts);
    }

    [Fact]
    public async Task InvokeAsync_VersionOneEnvelope_MatchesSharedPythonContractFixture()
    {
        var handler = new CapturingHandler(
            Response(HttpStatusCode.OK, """{"status":"ok"}"""));
        var client = CreateClient(handler, maxAttempts: 1);
        var parameters = new Dictionary<string, object?>
        {
            ["user_message"] = "Dispute demo transaction DEMO-TXN-1001.",
            ["trace_id"] = "0123456789abcdef0123456789abcdef",
            ["workflow_id"] = "11111111-1111-1111-1111-111111111111",
            ["workflow_status"] = "specialist_processing",
            ["intent"] = "dispute",
            ["context"] = new Dictionary<string, object?>
            {
                ["planner_summary"] = "The planner selected dispute handling.",
                ["planner_evidence"] = new[] { "Dispute language was detected." },
                ["planner_selected_agent"] = "dispute-planning",
                ["selected_agent"] = "dispute-planning"
            }
        };

        var result = await client.InvokeAsync("workflow.plan", parameters);

        Assert.Equal("ok", result.Status);
        var expected = JsonNode.Parse(
            await File.ReadAllTextAsync(ContractFixturePath()));
        var actual = JsonNode.Parse(Assert.IsType<string>(handler.RequestBody));
        Assert.True(
            JsonNode.DeepEquals(expected, actual),
            $"Expected:{Environment.NewLine}{expected}{Environment.NewLine}Actual:{Environment.NewLine}{actual}");
    }

    [Fact]
    public async Task InvokeAsync_PlannerEnvelope_EmitsEmptyTopLevelContext()
    {
        var handler = new CapturingHandler(
            Response(HttpStatusCode.OK, """{"status":"ok"}"""));
        var client = CreateClient(handler, maxAttempts: 1);

        await client.InvokeAsync(
            "workflow.plan",
            new Dictionary<string, object?>
            {
                ["user_message"] = "Explain this transaction.",
                ["trace_id"] = "0123456789abcdef0123456789abcdef",
                ["workflow_id"] = "11111111-1111-1111-1111-111111111111"
            });

        var request = JsonNode.Parse(Assert.IsType<string>(handler.RequestBody));
        var context = Assert.IsType<JsonObject>(request!["context"]);
        Assert.Empty(context);
    }

    [Fact]
    public async Task InvokeAsync_AddsMcpMetadataAndCallEnvelope()
    {
        var handler = new CapturingHandler(
            Response(HttpStatusCode.OK, """{"status":"ok"}"""));
        var client = CreateClient(handler, maxAttempts: 1);

        await client.InvokeAsync("workflow.plan", Parameters());

        var request = JsonNode.Parse(Assert.IsType<string>(handler.RequestBody));
        var metadata = Assert.IsType<JsonObject>(request!["metadata"]);
        Assert.Equal("jsonrpc-2.0", metadata["mcp_protocol"]?.GetValue<string>());
        Assert.Equal("tools/call", metadata["mcp_method"]?.GetValue<string>());
        Assert.Equal("workflow.plan", metadata["mcp_tool_name"]?.GetValue<string>());
        var mcp = Assert.IsType<JsonObject>(request["mcp"]);
        Assert.Equal("tools/call", mcp["method"]?.GetValue<string>());
        Assert.Equal("workflow.plan", mcp["params"]?["name"]?.GetValue<string>());
    }

    [Fact]
    public async Task DiscoverToolsAsync_RemoteCatalog_PopulatesToolDefinitions()
    {
        var handler = new CapturingHandler(
            Response(HttpStatusCode.OK, """{"tools":[{"name":"workflow.plan","description":"Planner","endpoint":"https://example.test/planner"}]}"""));
        var client = CreateClient(handler, maxAttempts: 1, discoveryEndpoint: "https://example.test/discover");

        var tools = await client.DiscoverToolsAsync("workflow-planning");

        Assert.Single(tools);
        Assert.Equal("workflow.plan", tools[0].Name);
        Assert.Equal("https://example.test/planner", tools[0].Endpoint);
    }

    [Fact]
    public async Task DiscoverToolsAsync_McpEnvelope_PopulatesTypedSchemas()
    {
        var handler = new CapturingHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"1","result":{"tools":[{"name":"workflow.plan","description":"Planner","endpoint":"https://example.test/planner","inputSchema":{"type":"object","required":["user_message"]},"outputSchema":{"type":"object"}}]}}"""));
        var client = CreateClient(handler, maxAttempts: 1, discoveryEndpoint: "https://example.test/discover");

        var tools = await client.DiscoverToolsAsync("workflow-planning");

        var tool = Assert.Single(tools);
        Assert.Equal("workflow.plan", tool.Name);
        Assert.NotNull(tool.InputSchema);
        Assert.NotNull(tool.OutputSchema);
        Assert.Contains("user_message", tool.InputSchema.Keys);
    }

    private static FoundryMcpClient CreateClient(
        HttpMessageHandler handler,
        int maxAttempts) =>
        new(
            new HttpClient(handler),
            NullLogger<FoundryMcpClient>.Instance,
            Options.Create(new FoundryMcpClientOptions
            {
                DefaultEndpoint = "https://example.test/agent",
                MaxAttempts = maxAttempts,
                AttemptTimeoutSeconds = 1,
                BaseDelayMilliseconds = 0
            }));

    private static FoundryMcpClient CreateClient(
        HttpMessageHandler handler,
        int maxAttempts,
        string discoveryEndpoint) =>
        new(
            new HttpClient(handler),
            NullLogger<FoundryMcpClient>.Instance,
            Options.Create(new FoundryMcpClientOptions
            {
                DefaultEndpoint = "https://example.test/agent",
                DiscoveryEndpoint = discoveryEndpoint,
                MaxAttempts = maxAttempts,
                AttemptTimeoutSeconds = 1,
                BaseDelayMilliseconds = 0
            }));

    private static Dictionary<string, object?> Parameters() =>
        new()
        {
            ["workflow_id"] = Guid.NewGuid().ToString(),
            ["trace_id"] = Guid.NewGuid().ToString("N"),
            ["user_message"] = "Synthetic request"
        };

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string body = """{"error":{"code":"synthetic"}}""") =>
        new(statusCode)
        {
            Content = new StringContent(body)
        };

    private static string ContractFixturePath() =>
        Path.Combine(
            AppContext.BaseDirectory,
            "fixtures",
            "hosted-agent-invocation-v1.json");

    private sealed class CapturingHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        public string? RequestBody { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestBody = request.Content is null
                ? null
                : await request.Content.ReadAsStringAsync(cancellationToken);
            return response;
        }
    }

    private sealed class SequenceHandler(
        params Func<CancellationToken, Task<HttpResponseMessage>>[] attempts)
        : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            return attempts[Math.Min(attempt, attempts.Length) - 1](cancellationToken);
        }
    }
}
