using System.Net;
using System.Text.Json.Nodes;
using BankingAgent.Application;
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
            ["correlation_id"] = "corr-123",
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
    public async Task InvokeAsync_LegacyEnvelope_DoesNotClaimMcpTransport()
    {
        var handler = new CapturingHandler(
            Response(HttpStatusCode.OK, """{"status":"ok"}"""));
        var client = CreateClient(handler, maxAttempts: 1);

        var parameters = Parameters();
        parameters["correlation_id"] = "corr-123";
        await client.InvokeAsync("workflow.plan", parameters);

        var request = JsonNode.Parse(Assert.IsType<string>(handler.RequestBody));
        var metadata = Assert.IsType<JsonObject>(request!["metadata"]);
        Assert.Equal("typed-envelope-v1", metadata["transport"]?.GetValue<string>());
        Assert.Equal("corr-123", metadata["correlation_id"]?.GetValue<string>());
        Assert.Equal("corr-123", request["correlation_id"]?.GetValue<string>());
        Assert.Null(request["mcp"]);
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
        Assert.Contains("required", tool.InputSchema.Keys);
    }

    [Fact]
    public async Task DiscoverToolsAsync_McpEndpoint_PerformsInitializeThenToolsList()
    {
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"init","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}"""),
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"list","result":{"tools":[{"name":"transaction.explain","description":"Explain","inputSchema":{"type":"object","properties":{"user_message":{"type":"string"},"trace_id":{"type":"string"},"workflow_id":{"type":"string"}},"required":["user_message","trace_id","workflow_id"]}}]}}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        var tools = await client.DiscoverToolsAsync();

        Assert.Contains(tools, tool => tool.Name == "transaction.explain" && tool.DiscoverySource == "mcp");
        Assert.Equal(["initialize", "tools/list"], handler.Methods);
    }

    [Fact]
    public async Task DiscoverToolsAsync_McpDiscoveryFailure_Throws()
    {
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.BadGateway, """{"error":"unavailable"}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        await Assert.ThrowsAsync<HttpRequestException>(() => client.DiscoverToolsAsync());
    }

    [Fact]
    public async Task InvokeAsync_McpUnknownToolName_ReturnsJsonRpcErrorResult()
    {
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"init","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}"""),
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"call","error":{"code":-32602,"message":"Unknown tool: transaction.explain"}}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        var result = await client.InvokeAsync("transaction.explain", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal("-32602", result.Data["error_code"]);
    }

    [Fact]
    public async Task InvokeAsync_McpToolCall_UnwrapsStructuredContentIntoResponseBody()
    {
        // Callers deserialise response_body directly into an agent result, so the
        // MCP envelope must not leak through. Returning the envelope makes every
        // agent field null and the workflow fails with "invalid agent result".
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"init","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}"""),
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"call","result":{"content":[{"type":"text","text":"{\"agent\":\"transaction-explanation\",\"status\":\"ok\"}"}],"structuredContent":{"agent":"transaction-explanation","status":"ok","summary":"Explained."}}}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        var result = await client.InvokeAsync("transaction.explain", Parameters());

        Assert.Equal("ok", result.Status);

        var responseBody = Assert.IsType<string>(result.Data["response_body"]);
        var parsed = JsonNode.Parse(responseBody)!.AsObject();

        Assert.Equal("transaction-explanation", parsed["agent"]!.GetValue<string>());
        Assert.Equal("Explained.", parsed["summary"]!.GetValue<string>());
        Assert.False(parsed.ContainsKey("structuredContent"));
        Assert.False(parsed.ContainsKey("content"));

        // The raw envelope stays available for diagnostics.
        Assert.Contains("structuredContent", Assert.IsType<string>(result.Data["mcp_envelope_body"]));
    }

    [Fact]
    public async Task InvokeAsync_McpToolCall_FallsBackToTextContentWhenNoStructuredContent()
    {
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"init","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}"""),
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"call","result":{"content":[{"type":"text","text":"{\"agent\":\"transaction-explanation\",\"status\":\"ok\",\"summary\":\"From text block.\"}"}]}}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        var result = await client.InvokeAsync("transaction.explain", Parameters());

        var responseBody = Assert.IsType<string>(result.Data["response_body"]);
        var parsed = JsonNode.Parse(responseBody)!.AsObject();

        Assert.Equal("From text block.", parsed["summary"]!.GetValue<string>());
    }

    [Fact]
    public async Task ValidateRequiredToolsAsync_McpSchemaMismatch_Throws()
    {
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"init","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}"""),
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"list","result":{"tools":[{"name":"transaction.explain","description":"Explain","inputSchema":{"type":"object","properties":{"user_message":{"type":"string"}},"required":["user_message"]}}]}}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ValidateRequiredToolsAsync(
                [new McpRequiredTool("transaction.explain", ["user_message", "trace_id", "workflow_id"])]));
    }

    [Fact]
    public async Task ValidateRequiredToolsAsync_MissingMcpTool_Throws()
    {
        var handler = new CapturingSequenceHandler(
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"init","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}"""),
            Response(HttpStatusCode.OK, """{"jsonrpc":"2.0","id":"list","result":{"tools":[{"name":"transaction.rename","description":"Renamed","inputSchema":{"type":"object","properties":{"user_message":{"type":"string"},"trace_id":{"type":"string"},"workflow_id":{"type":"string"}},"required":["user_message","trace_id","workflow_id"]}}]}}"""));
        var client = CreateClient(
            handler,
            maxAttempts: 1,
            mcpToolEndpointsJson: """{"transaction.explain":"https://example.test/mcp"}""");

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            client.ValidateRequiredToolsAsync(
                [new McpRequiredTool("transaction.explain", ["user_message", "trace_id", "workflow_id"])]));
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

    private static FoundryMcpClient CreateClient(
        HttpMessageHandler handler,
        int maxAttempts,
        string mcpToolEndpointsJson,
        bool useMcp = true) =>
        new(
            new HttpClient(handler),
            NullLogger<FoundryMcpClient>.Instance,
            Options.Create(new FoundryMcpClientOptions
            {
                ToolEndpointsJson = "{}",
                McpToolEndpointsJson = useMcp ? mcpToolEndpointsJson : "{}",
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

    [Fact]
    public async Task DiscoverToolsAsync_EveryAgentEndpoint_ContributesItsOwnTool()
    {
        var handler = new PerEndpointMcpHandler(new Dictionary<string, string?>
        {
            ["https://example.test/plan"] = "workflow.plan",
            ["https://example.test/explain"] = "transaction.explain",
            ["https://example.test/assess"] = "suspicious.assess",
            ["https://example.test/dispute"] = "dispute.plan"
        });
        var client = CreateClient(handler, maxAttempts: 1, mcpToolEndpointsJson: AllFourEndpointsJson);

        var tools = await client.DiscoverToolsAsync();

        Assert.Equal(
            ["dispute.plan", "suspicious.assess", "transaction.explain", "workflow.plan"],
            tools.Select(tool => tool.Name).OrderBy(name => name, StringComparer.Ordinal));
        Assert.All(tools, tool => Assert.Equal("mcp", tool.DiscoverySource));
        Assert.Equal(4, handler.InitializeCount);
    }

    [Fact]
    public async Task ValidateRequiredToolsAsync_AllFourAgentsHealthy_Succeeds()
    {
        var handler = new PerEndpointMcpHandler(new Dictionary<string, string?>
        {
            ["https://example.test/plan"] = "workflow.plan",
            ["https://example.test/explain"] = "transaction.explain",
            ["https://example.test/assess"] = "suspicious.assess",
            ["https://example.test/dispute"] = "dispute.plan"
        });
        var client = CreateClient(handler, maxAttempts: 1, mcpToolEndpointsJson: AllFourEndpointsJson);

        await client.ValidateRequiredToolsAsync(AllFourRequiredTools);
    }

    [Fact]
    public async Task ValidateRequiredToolsAsync_OneAgentMissingItsTool_Fails()
    {
        // A renamed or unmigrated agent must be caught at readiness rather than
        // when a workflow first routes to it.
        var handler = new PerEndpointMcpHandler(new Dictionary<string, string?>
        {
            ["https://example.test/plan"] = "workflow.plan",
            ["https://example.test/explain"] = "transaction.explain",
            ["https://example.test/assess"] = null,
            ["https://example.test/dispute"] = "dispute.plan"
        });
        var client = CreateClient(handler, maxAttempts: 1, mcpToolEndpointsJson: AllFourEndpointsJson);

        var exception = await Assert.ThrowsAsync<InvalidOperationException>(
            () => client.ValidateRequiredToolsAsync(AllFourRequiredTools));

        Assert.Contains("suspicious.assess: missing", exception.Message, StringComparison.Ordinal);
    }

    private const string AllFourEndpointsJson =
        """
        {
          "workflow.plan": "https://example.test/plan",
          "transaction.explain": "https://example.test/explain",
          "suspicious.assess": "https://example.test/assess",
          "dispute.plan": "https://example.test/dispute"
        }
        """;

    private static readonly McpRequiredTool[] AllFourRequiredTools =
    [
        new("workflow.plan", ["user_message", "trace_id", "workflow_id"]),
        new("transaction.explain", ["user_message", "trace_id", "workflow_id"]),
        new("suspicious.assess", ["user_message", "trace_id", "workflow_id"]),
        new("dispute.plan", ["user_message", "trace_id", "workflow_id"])
    ];

    /// <summary>
    /// Responds per endpoint rather than per call sequence, so multiple agents
    /// can be probed concurrently without the assertions depending on ordering.
    /// </summary>
    private sealed class PerEndpointMcpHandler(IReadOnlyDictionary<string, string?> toolByEndpoint)
        : HttpMessageHandler
    {
        private int _initializeCount;

        public int InitializeCount => Volatile.Read(ref _initializeCount);

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var requestJson = JsonNode.Parse(requestBody)!.AsObject();
            var method = requestJson["method"]?.GetValue<string>() ?? "";
            var id = requestJson["id"]?.GetValue<string>() ?? "1";

            if (method == "initialize")
            {
                Interlocked.Increment(ref _initializeCount);
                return Json(
                    """{"jsonrpc":"2.0","id":"__ID__","result":{"protocolVersion":"2024-11-05","capabilities":{"tools":{}}}}""",
                    id);
            }

            if (method != "tools/list")
            {
                return Json("""{"jsonrpc":"2.0","id":"__ID__","error":{"code":-32601,"message":"no"}}""", id);
            }

            var endpoint = request.RequestUri!.GetLeftPart(UriPartial.Path);
            var toolName = toolByEndpoint.TryGetValue(endpoint, out var name) ? name : null;
            var tools = toolName is null
                ? "[]"
                : """
                    [{"name":"__TOOL__","description":"d","inputSchema":{"type":"object","properties":{"user_message":{"type":"string"},"trace_id":{"type":"string"},"workflow_id":{"type":"string"}},"required":["user_message","trace_id","workflow_id"]},"outputSchema":{"type":"object"}}]
                    """.Replace("__TOOL__", toolName, StringComparison.Ordinal);

            return Json(
                """{"jsonrpc":"2.0","id":"__ID__","result":{"tools":__TOOLS__}}"""
                    .Replace("__TOOLS__", tools, StringComparison.Ordinal),
                id);
        }

        private static HttpResponseMessage Json(string body, string id) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(body.Replace("__ID__", id, StringComparison.Ordinal))
            };
    }

    private sealed class CapturingSequenceHandler(params HttpResponseMessage[] responses) : HttpMessageHandler
    {
        private int _attempts;

        public List<string> Methods { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var requestBody = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var requestJson = JsonNode.Parse(requestBody)!.AsObject();
            Methods.Add(requestJson["method"]?.GetValue<string>() ?? "");
            var id = requestJson["id"]?.GetValue<string>();

            var attempt = Interlocked.Increment(ref _attempts);
            var source = responses[Math.Min(attempt, responses.Length) - 1];
            var body = source.Content is null
                ? ""
                : await source.Content.ReadAsStringAsync(cancellationToken);
            if (!string.IsNullOrWhiteSpace(id))
            {
                body = body.Replace("\"id\":\"init\"", $"\"id\":\"{id}\"", StringComparison.Ordinal)
                    .Replace("\"id\":\"list\"", $"\"id\":\"{id}\"", StringComparison.Ordinal)
                    .Replace("\"id\":\"call\"", $"\"id\":\"{id}\"", StringComparison.Ordinal);
            }

            return new HttpResponseMessage(source.StatusCode)
            {
                Content = new StringContent(body)
            };
        }
    }
}
