using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using BankingAgent.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankingAgent.Infrastructure;

public sealed class FoundryMcpClientOptions
{
    public string? DefaultEndpoint { get; set; }
    public string? AgentName { get; set; }
    public string? DiscoveryEndpoint { get; set; }
    public string Scope { get; set; } = "https://ai.azure.com/.default";
    public string? ToolEndpointsJson { get; set; }
    public string? McpToolEndpointsJson { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int AttemptTimeoutSeconds { get; set; } = 30;
    public int BaseDelayMilliseconds { get; set; } = 250;
}

public sealed class FoundryMcpClient : IMcpClient
{
    private const string ContractVersion = "1.0";

    private readonly HttpClient _httpClient;
    private readonly ILogger<FoundryMcpClient> _logger;
    private readonly DefaultAzureCredential _credential = new(new DefaultAzureCredentialOptions
    {
        ExcludeVisualStudioCodeCredential = true,
        ExcludeInteractiveBrowserCredential = true
    });

    private readonly string? _defaultEndpoint;
    private readonly string? _agentName;
    private readonly string? _discoveryEndpoint;
    private readonly string _scope;
    private readonly IReadOnlyDictionary<string, string> _toolEndpoints;
    private readonly IReadOnlyDictionary<string, string> _mcpToolEndpoints;
    private readonly Dictionary<string, McpToolDefinition> _discoveredTools;
    private readonly FoundryMcpClientOptions _options;
    private static readonly IReadOnlyDictionary<string, object?> EmptySchema = new Dictionary<string, object?>
    {
        ["type"] = "object",
        ["properties"] = new Dictionary<string, object?>(),
        ["required"] = Array.Empty<string>()
    };

    public FoundryMcpClient(
        HttpClient httpClient,
        ILogger<FoundryMcpClient> logger,
        IOptions<FoundryMcpClientOptions>? options = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options?.Value ?? new FoundryMcpClientOptions();
        ValidateOptions(_options);
        _defaultEndpoint = _options.DefaultEndpoint ?? ReadSetting("FOUNDRY_AGENT_ENDPOINT");
        _agentName = _options.AgentName ?? ReadSetting("FOUNDRY_AGENT_NAME");
        _discoveryEndpoint = _options.DiscoveryEndpoint
            ?? ReadSetting("FOUNDRY_MCP_DISCOVERY_ENDPOINT")
            ?? ReadSetting("FOUNDRY_DISCOVERY_ENDPOINT");
        _scope = string.IsNullOrWhiteSpace(_options.Scope)
            ? ReadSetting("FOUNDRY_SCOPE") ?? "https://ai.azure.com/.default"
            : _options.Scope;
        _toolEndpoints = ReadToolEndpointMap(
            _options.ToolEndpointsJson ?? ReadSetting("FOUNDRY_TOOL_ENDPOINTS"));
        _mcpToolEndpoints = ReadToolEndpointMap(
            _options.McpToolEndpointsJson ?? ReadSetting("FOUNDRY_MCP_TOOL_ENDPOINTS"));
        _discoveredTools = new Dictionary<string, McpToolDefinition>(StringComparer.OrdinalIgnoreCase);
        foreach (var toolEndpoint in _toolEndpoints)
        {
            _discoveredTools[toolEndpoint.Key] = new McpToolDefinition(
                toolEndpoint.Key,
                "Tool endpoint from configuration",
                _agentName ?? toolEndpoint.Key,
                toolEndpoint.Value,
                CreateToolInputSchema(toolEndpoint.Key),
                "configuration",
                IsDefault: false,
                OutputSchema: CreateToolOutputSchema(toolEndpoint.Key));
        }

        if (!string.IsNullOrWhiteSpace(_defaultEndpoint))
        {
            _discoveredTools["default"] = new McpToolDefinition(
                "default",
                "Default Foundry endpoint",
                _agentName ?? "default",
                _defaultEndpoint,
                CreateToolInputSchema("default"),
                "configuration",
                IsDefault: true,
                OutputSchema: CreateToolOutputSchema("default"));
        }
    }

    public async Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(
        string? agentName = null,
        CancellationToken cancellationToken = default)
    {
        if (_mcpToolEndpoints.Count > 0)
        {
            foreach (var (toolName, endpoint) in _mcpToolEndpoints)
            {
                var discovered = await DiscoverMcpToolsAsync(toolName, endpoint, cancellationToken);
                foreach (var tool in discovered)
                {
                    _discoveredTools[tool.Name] = tool;
                }
            }
        }

        if (!string.IsNullOrWhiteSpace(_discoveryEndpoint))
        {
            try
            {
                var mcpDiscoveryRequest = BuildDiscoveryRequest(agentName);
                using var request = new HttpRequestMessage(HttpMethod.Post, _discoveryEndpoint)
                {
                    Content = JsonContent.Create(new
                    {
                        contract_version = ContractVersion,
                        operation = "discover",
                        agent_name = agentName ?? ResolveAgentName("default"),
                        input = new Dictionary<string, object?>
                        {
                            ["agent_name"] = agentName ?? ResolveAgentName("default")
                        },
                        mcp = mcpDiscoveryRequest
                    })
                };

                if (RequiresFoundryToken(_discoveryEndpoint))
                {
                    var token = await _credential.GetTokenAsync(
                        new TokenRequestContext([_scope]),
                        cancellationToken);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                }

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);
                var body = await response.Content.ReadAsStringAsync(cancellationToken);
                if (response.IsSuccessStatusCode)
                {
                    var discovered = ParseDiscoveredTools(body, agentName);
                    if (discovered.Count > 0)
                    {
                        _discoveredTools.Clear();
                        foreach (var tool in discovered)
                        {
                            _discoveredTools[tool.Name] = tool;
                        }

                        foreach (var toolEndpoint in _toolEndpoints)
                        {
                            _discoveredTools[toolEndpoint.Key] = new McpToolDefinition(
                                toolEndpoint.Key,
                                "Tool endpoint from configuration",
                                _agentName ?? toolEndpoint.Key,
                                toolEndpoint.Value,
                                CreateToolInputSchema(toolEndpoint.Key),
                                "configuration",
                                IsDefault: false,
                                OutputSchema: CreateToolOutputSchema(toolEndpoint.Key));
                        }

                        if (!string.IsNullOrWhiteSpace(_defaultEndpoint))
                        {
                            _discoveredTools["default"] = new McpToolDefinition(
                                "default",
                                "Default Foundry endpoint",
                                _agentName ?? "default",
                                _defaultEndpoint,
                                CreateToolInputSchema("default"),
                                "configuration",
                                IsDefault: true,
                                OutputSchema: CreateToolOutputSchema("default"));
                        }

                        return discovered;
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Unable to discover legacy Foundry adapter tools from {DiscoveryEndpoint}", _discoveryEndpoint);
            }
        }

        return _discoveredTools.Values.OrderBy(tool => tool.Name).ToList();
    }

    public async Task ValidateRequiredToolsAsync(
        IReadOnlyCollection<McpRequiredTool> requiredTools,
        CancellationToken cancellationToken = default)
    {
        var discoveredTools = await DiscoverToolsAsync(cancellationToken: cancellationToken);
        var discoveredByName = discoveredTools.ToDictionary(tool => tool.Name, StringComparer.OrdinalIgnoreCase);
        var failures = new List<string>();

        foreach (var requiredTool in requiredTools)
        {
            if (!discoveredByName.TryGetValue(requiredTool.Name, out var tool))
            {
                failures.Add($"{requiredTool.Name}: missing");
                continue;
            }

            var missingProperties = FindMissingRequiredInputProperties(
                tool.InputSchema,
                requiredTool.RequiredInputProperties);
            if (missingProperties.Count > 0)
            {
                failures.Add($"{requiredTool.Name}: schema missing {string.Join(", ", missingProperties)}");
            }
        }

        if (failures.Count > 0)
        {
            throw new InvalidOperationException(
                $"Required MCP tool validation failed: {string.Join("; ", failures)}.");
        }
    }

    public async Task<McpToolResult> InvokeAsync(string toolName, IDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        if (_mcpToolEndpoints.TryGetValue(toolName, out var mcpEndpoint))
        {
            return await InvokeMcpToolAsync(toolName, mcpEndpoint, parameters, cancellationToken);
        }

        var endpoint = ResolveEndpoint(toolName);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("No Foundry endpoint configured for {ToolName}; returning a configuration error result", toolName);
            return CreateMissingEndpointErrorResult(toolName, parameters, endpoint);
        }

        var traceId = parameters.TryGetValue("trace_id", out var traceIdValue) ? traceIdValue?.ToString() : null;
        var workflowId = parameters.TryGetValue("workflow_id", out var workflowIdValue)
            ? workflowIdValue?.ToString()
            : null;
        var correlationId = parameters.TryGetValue("correlation_id", out var correlationIdValue)
            ? correlationIdValue?.ToString()
            : null;
        var context = parameters.TryGetValue("context", out var contextValue) &&
            contextValue is not null
                ? contextValue
                : new Dictionary<string, object?>();
        var payload = new
        {
            contract_version = ContractVersion,
            tool_name = toolName,
            agent_name = ResolveAgentName(toolName),
            input = parameters,
            trace_id = traceId,
            workflow_id = workflowId,
            correlation_id = correlationId,
            message = parameters.TryGetValue("user_message", out var userMessage) ? userMessage?.ToString() : null,
            metadata = new
            {
                tool_name = toolName,
                trace_id = traceId,
                workflow_id = workflowId,
                correlation_id = correlationId,
                transport = "typed-envelope-v1"
            },
            context
        };

        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds));
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
                {
                    Content = JsonContent.Create(payload)
                };

                if (RequiresFoundryToken(endpoint))
                {
                    var token = await _credential.GetTokenAsync(
                        new TokenRequestContext([_scope]),
                        attemptCancellation.Token);
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
                }

                request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
                if (parameters.TryGetValue("correlation_id", out var correlationIdHeaderValue) &&
                    correlationIdHeaderValue is not null)
                {
                    request.Headers.TryAddWithoutValidation(
                        "x-correlation-id",
                        correlationIdHeaderValue.ToString());
                }

                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    attemptCancellation.Token);
                var body = await response.Content.ReadAsStringAsync(attemptCancellation.Token);
                if (IsTransient(response.StatusCode) && attempt < _options.MaxAttempts)
                {
                    await DelayBeforeRetryAsync(
                        toolName,
                        attempt,
                        $"HTTP {(int)response.StatusCode}",
                        cancellationToken);
                    continue;
                }

                return CreateResponseResult(toolName, endpoint, response, body, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                if (attempt < _options.MaxAttempts)
                {
                    await DelayBeforeRetryAsync(
                        toolName,
                        attempt,
                        "attempt timeout",
                        cancellationToken);
                    continue;
                }

                return CreateInvocationErrorResult(
                    toolName,
                    endpoint,
                    ex,
                    "timeout",
                    attempt);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < _options.MaxAttempts)
                {
                    await DelayBeforeRetryAsync(
                        toolName,
                        attempt,
                        ex.GetType().Name,
                        cancellationToken);
                    continue;
                }

                return CreateInvocationErrorResult(
                    toolName,
                    endpoint,
                    ex,
                    "transport_error",
                    attempt);
            }
            catch (RequestFailedException ex) when (
                IsTransient(ex.Status) &&
                attempt < _options.MaxAttempts)
            {
                await DelayBeforeRetryAsync(
                    toolName,
                    attempt,
                    $"Azure HTTP {ex.Status}",
                    cancellationToken);
            }
            catch (AuthenticationFailedException ex)
            {
                return CreateInvocationErrorResult(
                    toolName,
                    endpoint,
                    ex,
                    "authentication_failed",
                    attempt);
            }
            catch (RequestFailedException ex)
            {
                return CreateInvocationErrorResult(
                    toolName,
                    endpoint,
                    ex,
                    "azure_request_failed",
                    attempt);
            }
        }

        throw new UnreachableException();
    }

    private async Task<IReadOnlyList<McpToolDefinition>> DiscoverMcpToolsAsync(
        string expectedToolName,
        string endpoint,
        CancellationToken cancellationToken)
    {
        await SendJsonRpcAsync(
            endpoint,
            BuildInitializeRequest(expectedToolName),
            cancellationToken);

        var response = await SendJsonRpcAsync(
            endpoint,
            BuildToolsListRequest(expectedToolName),
            cancellationToken);

        if (!response.TryGetProperty("result", out var result) ||
            !result.TryGetProperty("tools", out var tools) ||
            tools.ValueKind != JsonValueKind.Array)
        {
            throw new InvalidOperationException(
                $"MCP tools/list response from {endpoint} did not include a tools array.");
        }

        return tools.EnumerateArray()
            .Select(tool => ReadMcpToolDefinition(tool, endpoint))
            .Where(tool => tool is not null)
            .Select(tool => tool!)
            .ToList();
    }

    private async Task<McpToolResult> InvokeMcpToolAsync(
        string toolName,
        string endpoint,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= _options.MaxAttempts; attempt++)
        {
            using var attemptCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            attemptCancellation.CancelAfter(TimeSpan.FromSeconds(_options.AttemptTimeoutSeconds));
            try
            {
                await SendJsonRpcAsync(
                    endpoint,
                    BuildInitializeRequest(toolName),
                    attemptCancellation.Token);
                var response = await SendJsonRpcAsync(
                    endpoint,
                    BuildToolsCallRequest(toolName, parameters),
                    attemptCancellation.Token);
                return CreateMcpResponseResult(toolName, endpoint, response, attempt);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (OperationCanceledException ex)
            {
                if (attempt < _options.MaxAttempts)
                {
                    await DelayBeforeRetryAsync(toolName, attempt, "MCP attempt timeout", cancellationToken);
                    continue;
                }

                return CreateInvocationErrorResult(toolName, endpoint, ex, "timeout", attempt);
            }
            catch (HttpRequestException ex)
            {
                if (attempt < _options.MaxAttempts)
                {
                    await DelayBeforeRetryAsync(toolName, attempt, ex.GetType().Name, cancellationToken);
                    continue;
                }

                return CreateInvocationErrorResult(toolName, endpoint, ex, "transport_error", attempt);
            }
            catch (McpProtocolException ex)
            {
                return new McpToolResult(toolName, "error", "MCP tool invocation failed.", new Dictionary<string, object?>
                {
                    ["endpoint"] = endpoint,
                    ["error_code"] = ex.Code.ToString(),
                    ["error_message"] = ex.Message,
                    ["attempts"] = attempt,
                    ["transport"] = "mcp-jsonrpc-2.0"
                });
            }
        }

        throw new UnreachableException();
    }

    private async Task<JsonElement> SendJsonRpcAsync(
        string endpoint,
        Dictionary<string, object?> payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = JsonContent.Create(payload)
        };

        if (RequiresFoundryToken(endpoint))
        {
            var token = await _credential.GetTokenAsync(
                new TokenRequestContext([_scope]),
                cancellationToken);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        }

        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"MCP endpoint returned HTTP {(int)response.StatusCode}.",
                null,
                response.StatusCode);
        }

        using var document = JsonDocument.Parse(body);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("jsonrpc", out var jsonRpc) ||
            jsonRpc.GetString() != "2.0")
        {
            throw new McpProtocolException(-32600, "MCP response is not a JSON-RPC 2.0 object.");
        }

        if (payload.TryGetValue("id", out var requestId) &&
            requestId is not null &&
            !root.TryGetProperty("id", out _))
        {
            throw new McpProtocolException(-32600, "MCP response did not include the request id.");
        }

        if (payload.TryGetValue("id", out requestId) &&
            requestId is not null &&
            root.TryGetProperty("id", out var responseId) &&
            responseId.ToString() != requestId.ToString())
        {
            throw new McpProtocolException(-32600, "MCP response id does not match the request id.");
        }

        if (root.TryGetProperty("error", out var error) &&
            error.ValueKind == JsonValueKind.Object)
        {
            var code = error.TryGetProperty("code", out var codeElement) &&
                codeElement.ValueKind == JsonValueKind.Number &&
                codeElement.TryGetInt32(out var numericCode)
                    ? numericCode
                    : -32603;
            var message = error.TryGetProperty("message", out var messageElement) &&
                messageElement.ValueKind == JsonValueKind.String
                    ? messageElement.GetString() ?? "MCP error."
                    : "MCP error.";
            throw new McpProtocolException(code, message);
        }

        if (!root.TryGetProperty("result", out _))
        {
            throw new McpProtocolException(-32600, "MCP response did not include result or error.");
        }

        return root.Clone();
    }

    private string ResolveEndpoint(string toolName)
    {
        if (_discoveredTools.TryGetValue(toolName, out var discoveredTool))
        {
            return discoveredTool.Endpoint;
        }

        if (_toolEndpoints.TryGetValue(toolName, out var configuredToolEndpoint))
        {
            return configuredToolEndpoint;
        }

        return _defaultEndpoint ?? string.Empty;
    }

    private string ResolveAgentName(string toolName) => _agentName ?? toolName;

    private static bool RequiresFoundryToken(string endpoint) =>
        Uri.TryCreate(endpoint, UriKind.Absolute, out var uri) &&
        (uri.Host.EndsWith(".services.ai.azure.com", StringComparison.OrdinalIgnoreCase) ||
         uri.Host.EndsWith(".openai.azure.com", StringComparison.OrdinalIgnoreCase));

    private static string? ReadSetting(string key) => Environment.GetEnvironmentVariable(key);

    private static void ValidateOptions(FoundryMcpClientOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.MaxAttempts, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.MaxAttempts, 5);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.AttemptTimeoutSeconds, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.AttemptTimeoutSeconds, 120);
        ArgumentOutOfRangeException.ThrowIfNegative(options.BaseDelayMilliseconds);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.BaseDelayMilliseconds, 5000);
    }

    private static IReadOnlyDictionary<string, string> ReadToolEndpointMap(string? rawValue)
    {
        if (string.IsNullOrWhiteSpace(rawValue))
        {
            return new Dictionary<string, string>();
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<Dictionary<string, string>>(rawValue, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed ?? new Dictionary<string, string>();
        }
        catch (JsonException ex)
        {
            throw new InvalidOperationException(
                "FOUNDRY_TOOL_ENDPOINTS must be a JSON object mapping tool names to absolute endpoint URLs.",
                ex);
        }
    }

    private static IReadOnlyList<McpToolDefinition> ParseDiscoveredTools(string responseBody, string? agentName)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return [];
            }

            if (document.RootElement.TryGetProperty("result", out var result) &&
                result.ValueKind == JsonValueKind.Object)
            {
                if (result.TryGetProperty("tools", out var tools) && tools.ValueKind == JsonValueKind.Array)
                {
                    return tools.EnumerateArray()
                        .Select(tool => ReadToolDefinition(tool, agentName))
                        .Where(definition => definition is not null)
                        .Select(definition => definition!)
                        .ToList();
                }
            }

            if (document.RootElement.TryGetProperty("tools", out var toolsArray) &&
                toolsArray.ValueKind == JsonValueKind.Array)
            {
                return toolsArray.EnumerateArray()
                    .Select(tool => ReadToolDefinition(tool, agentName))
                    .Where(definition => definition is not null)
                    .Select(definition => definition!)
                    .ToList();
            }

            if (document.RootElement.TryGetProperty("tool_definitions", out var toolDefinitions) &&
                toolDefinitions.ValueKind == JsonValueKind.Array)
            {
                return toolDefinitions.EnumerateArray()
                    .Select(tool => ReadToolDefinition(tool, agentName))
                    .Where(definition => definition is not null)
                    .Select(definition => definition!)
                    .ToList();
            }
        }
        catch (JsonException)
        {
            // Fall back to an empty catalog when the response is not parseable.
        }

        return [];
    }

    private static McpToolDefinition? ReadToolDefinition(JsonElement tool, string? agentName)
    {
        if (tool.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var name = tool.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString()
            : null;
        var description = tool.TryGetProperty("description", out var descriptionElement) && descriptionElement.ValueKind == JsonValueKind.String
            ? descriptionElement.GetString()
            : null;
        var endpoint = tool.TryGetProperty("endpoint", out var endpointElement) && endpointElement.ValueKind == JsonValueKind.String
            ? endpointElement.GetString()
            : null;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(endpoint))
        {
            return null;
        }

        return new McpToolDefinition(
            name!,
            description ?? "Discovered Foundry MCP tool",
            agentName ?? name!,
            endpoint!,
            ReadToolInputSchema(tool),
            "discovery",
            IsDefault: false,
            OutputSchema: ReadToolOutputSchema(tool));
    }

    private static object? TryParseResponse(string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return null;
        }

        try
        {
            return JsonSerializer.Deserialize<object>(responseBody);
        }
        catch (JsonException)
        {
            return responseBody;
        }
    }

    private static Dictionary<string, object?> BuildDiscoveryRequest(string? agentName) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = CreateRequestId("tools/list", agentName ?? "default"),
        ["method"] = "tools/list",
        ["params"] = new Dictionary<string, object?>
        {
            ["cursor"] = (string?)null,
            ["agent_name"] = agentName ?? "default"
        }
    };

    private static Dictionary<string, object?> BuildInitializeRequest(string toolName) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = CreateRequestId("initialize", toolName),
        ["method"] = "initialize",
        ["params"] = new Dictionary<string, object?>
        {
            ["protocolVersion"] = "2024-11-05",
            ["capabilities"] = new Dictionary<string, object?>(),
            ["clientInfo"] = new Dictionary<string, object?>
            {
                ["name"] = "banking-agent-orchestrator",
                ["version"] = ContractVersion
            }
        }
    };

    private static Dictionary<string, object?> BuildToolsListRequest(string toolName) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = CreateRequestId("tools/list", toolName),
        ["method"] = "tools/list",
        ["params"] = new Dictionary<string, object?>()
    };

    private static Dictionary<string, object?> BuildToolsCallRequest(string toolName, IDictionary<string, object?> parameters) => new()
    {
        ["jsonrpc"] = "2.0",
        ["id"] = CreateRequestId("tools/call", toolName, parameters),
        ["method"] = "tools/call",
        ["params"] = new Dictionary<string, object?>
        {
            ["name"] = toolName,
            ["arguments"] = parameters
        }
    };

    private static string CreateRequestId(string method, string? seed)
    {
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{method}:{seed ?? "default"}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static string CreateRequestId(string method, string toolName, IDictionary<string, object?> parameters)
    {
        var traceId = parameters.TryGetValue("trace_id", out var traceIdValue)
            ? traceIdValue?.ToString() ?? "trace:none"
            : "trace:none";
        var workflowId = parameters.TryGetValue("workflow_id", out var workflowIdValue)
            ? workflowIdValue?.ToString() ?? "workflow:none"
            : "workflow:none";
        var hash = SHA256.HashData(Encoding.UTF8.GetBytes($"{method}:{toolName}:{traceId}:{workflowId}"));
        return Convert.ToHexString(hash)[..16].ToLowerInvariant();
    }

    private static IReadOnlyDictionary<string, object?> CreateToolInputSchema(string toolName)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["user_message"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The customer request or workflow prompt."
            },
            ["trace_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The workflow trace identifier."
            },
            ["workflow_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The durable workflow identifier."
            },
            ["context"] = new Dictionary<string, object?>
            {
                ["type"] = "object",
                ["description"] = "Context passed from the orchestrator to the specialist."
            },
            ["correlation_id"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "Workflow correlation identifier used for tracing and observability."
            },
            ["workflow_status"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The orchestrator workflow status."
            },
            ["intent"] = new Dictionary<string, object?>
            {
                ["type"] = "string",
                ["description"] = "The orchestrator intent when already known."
            }
        };

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "object",
            ["title"] = $"{toolName} input",
            ["properties"] = properties,
            ["required"] = new[] { "user_message", "trace_id", "workflow_id" },
            ["additionalProperties"] = true
        };
    }

    private static IReadOnlyDictionary<string, object?> CreateToolOutputSchema(string toolName)
    {
        var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["agent"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["status"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["execution_mode"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["contract_version"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["intent"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["summary"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["requires_approval"] = new Dictionary<string, object?> { ["type"] = "boolean" },
            ["selected_agent"] = new Dictionary<string, object?> { ["type"] = "string" },
            ["evidence"] = new Dictionary<string, object?> { ["type"] = "array" }
        };

        return new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase)
        {
            ["type"] = "object",
            ["title"] = $"{toolName} output",
            ["properties"] = properties,
            ["additionalProperties"] = true
        };
    }

    private static IReadOnlyDictionary<string, object?> ReadToolInputSchema(JsonElement tool)
    {
        if (tool.TryGetProperty("inputSchema", out var inputSchemaElement) &&
            inputSchemaElement.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(inputSchemaElement.GetRawText())
                ?? EmptySchema;
        }

        return CreateToolInputSchema(tool.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? "tool"
            : "tool");
    }

    private static IReadOnlyDictionary<string, object?> ReadToolOutputSchema(JsonElement tool)
    {
        if (tool.TryGetProperty("outputSchema", out var outputSchemaElement) &&
            outputSchemaElement.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(outputSchemaElement.GetRawText())
                ?? EmptySchema;
        }

        return CreateToolOutputSchema(tool.TryGetProperty("name", out var nameElement) && nameElement.ValueKind == JsonValueKind.String
            ? nameElement.GetString() ?? "tool"
            : "tool");
    }

    private static McpToolDefinition? ReadMcpToolDefinition(JsonElement tool, string endpoint)
    {
        if (tool.ValueKind != JsonValueKind.Object ||
            !tool.TryGetProperty("name", out var nameElement) ||
            nameElement.ValueKind != JsonValueKind.String)
        {
            return null;
        }

        var name = nameElement.GetString();
        if (string.IsNullOrWhiteSpace(name))
        {
            return null;
        }

        var description = tool.TryGetProperty("description", out var descriptionElement) &&
            descriptionElement.ValueKind == JsonValueKind.String
                ? descriptionElement.GetString() ?? "Discovered MCP tool"
                : "Discovered MCP tool";

        return new McpToolDefinition(
            name,
            description,
            name,
            endpoint,
            ReadToolInputSchema(tool),
            "mcp",
            IsDefault: false,
            OutputSchema: ReadToolOutputSchema(tool));
    }

    private static IReadOnlyList<string> FindMissingRequiredInputProperties(
        IReadOnlyDictionary<string, object?> schema,
        IReadOnlyCollection<string> requiredProperties)
    {
        var propertyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (schema.TryGetValue("properties", out var properties))
        {
            switch (properties)
            {
                case JsonElement propertiesElement when propertiesElement.ValueKind == JsonValueKind.Object:
                    foreach (var property in propertiesElement.EnumerateObject())
                    {
                        propertyNames.Add(property.Name);
                    }
                    break;
                case IDictionary<string, object?> propertiesDictionary:
                    foreach (var property in propertiesDictionary.Keys)
                    {
                        propertyNames.Add(property);
                    }
                    break;
            }
        }

        if (propertyNames.Count == 0)
        {
            foreach (var key in schema.Keys)
            {
                propertyNames.Add(key);
            }
        }

        return requiredProperties
            .Where(required => !propertyNames.Contains(required))
            .ToList();
    }

    private static IReadOnlyDictionary<string, object?> ReadSchemaProperties(JsonElement schema)
    {
        if (schema.TryGetProperty("properties", out var propertiesElement) &&
            propertiesElement.ValueKind == JsonValueKind.Object)
        {
            return JsonSerializer.Deserialize<Dictionary<string, object?>>(propertiesElement.GetRawText())
                ?? EmptySchema;
        }

        if (schema.TryGetProperty("required", out var requiredElement) &&
            requiredElement.ValueKind == JsonValueKind.Array)
        {
            var properties = new Dictionary<string, object?>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in requiredElement.EnumerateArray())
            {
                if (item.ValueKind == JsonValueKind.String)
                {
                    properties[item.GetString() ?? string.Empty] = new Dictionary<string, object?>
                    {
                        ["type"] = "string",
                        ["required"] = true
                    };
                }
            }

            if (properties.Count > 0)
            {
                return properties;
            }
        }

        return JsonSerializer.Deserialize<Dictionary<string, object?>>(schema.GetRawText())
            ?? EmptySchema;
    }

    private static (string? Code, string? Message) ReadError(object? response)
    {
        if (response is not JsonElement root ||
            !root.TryGetProperty("error", out var error) ||
            error.ValueKind != JsonValueKind.Object)
        {
            return (null, null);
        }

        var code = error.TryGetProperty("code", out var codeElement)
            ? codeElement.GetString()
            : null;
        var message = error.TryGetProperty("message", out var messageElement)
            ? messageElement.GetString()
            : null;
        return (code, message);
    }

    private static string? HeaderValue(HttpResponseMessage response, string name) =>
        response.Headers.TryGetValues(name, out var values)
            ? values.FirstOrDefault()
            : null;

    private McpToolResult CreateResponseResult(
        string toolName,
        string endpoint,
        HttpResponseMessage response,
        string body,
        int attempt)
    {
        var parsedResponse = TryParseResponse(body);
        var (errorCode, errorMessage) = ReadError(parsedResponse);
        var agentInvocationId = HeaderValue(response, "x-agent-invocation-id");
        var agentSessionId = HeaderValue(response, "x-agent-session-id");
        var data = new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["agent_name"] = ResolveAgentName(toolName),
            ["status_code"] = (int)response.StatusCode,
            ["response_body"] = body,
            ["response_json"] = parsedResponse,
            ["error_code"] = errorCode,
            ["error_message"] = errorMessage,
            ["agent_invocation_id"] = agentInvocationId,
            ["agent_session_id"] = agentSessionId,
            ["attempts"] = attempt,
            ["transport"] = "typed-envelope-v1"
        };

        var message = response.IsSuccessStatusCode
            ? "Foundry adapter invocation completed successfully."
            : $"Foundry adapter returned HTTP {(int)response.StatusCode}"
                + (string.IsNullOrWhiteSpace(errorCode) ? "." : $" ({errorCode}).");

        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Foundry invocation failed for {ToolName} with HTTP {StatusCode}, error {ErrorCode}, and {Attempts} attempt(s). InvocationId={AgentInvocationId}, SessionId={AgentSessionId}",
                toolName,
                (int)response.StatusCode,
                errorCode,
                attempt,
                agentInvocationId,
                agentSessionId);
        }

        return response.IsSuccessStatusCode
            ? new McpToolResult(toolName, "ok", message, data)
            : new McpToolResult(toolName, "error", message, data);
    }

    private static McpToolResult CreateMcpResponseResult(
        string toolName,
        string endpoint,
        JsonElement response,
        int attempt)
    {
        var resultElement = response.GetProperty("result");
        var envelopeJson = resultElement.GetRawText();
        var isError = resultElement.TryGetProperty("isError", out var isErrorElement) &&
            isErrorElement.ValueKind is JsonValueKind.True;

        // Callers deserialise response_body straight into an agent result, so the
        // MCP envelope has to be unwrapped here. Leaving the envelope in place
        // makes every agent field deserialise to null and the result is rejected
        // as invalid. Keeping this in the transport adapter is what allows the
        // application layer to stay unaware of which transport was used.
        var agentPayloadJson = ExtractMcpAgentPayload(resultElement) ?? envelopeJson;
        var agentPayloadObject = JsonSerializer.Deserialize<object>(agentPayloadJson);

        return new McpToolResult(
            toolName,
            isError ? "error" : "ok",
            isError ? "MCP tool returned an error result." : "MCP tool invocation completed successfully.",
            new Dictionary<string, object?>
            {
                ["endpoint"] = endpoint,
                ["response_json"] = agentPayloadObject,
                ["response_body"] = agentPayloadJson,
                ["mcp_envelope_body"] = envelopeJson,
                ["attempts"] = attempt,
                ["transport"] = "mcp-jsonrpc-2.0",
                ["mcp_method"] = "tools/call",
                ["mcp_tool_name"] = toolName
            });
    }

    /// <summary>
    /// Pulls the agent payload out of an MCP tools/call result, preferring
    /// structuredContent and falling back to the first text content block.
    /// </summary>
    private static string? ExtractMcpAgentPayload(JsonElement resultElement)
    {
        if (resultElement.TryGetProperty("structuredContent", out var structured) &&
            structured.ValueKind is JsonValueKind.Object)
        {
            return structured.GetRawText();
        }

        if (!resultElement.TryGetProperty("content", out var content) ||
            content.ValueKind is not JsonValueKind.Array)
        {
            return null;
        }

        foreach (var block in content.EnumerateArray())
        {
            if (block.ValueKind is not JsonValueKind.Object ||
                !block.TryGetProperty("type", out var type) ||
                type.ValueKind is not JsonValueKind.String ||
                !string.Equals(type.GetString(), "text", StringComparison.Ordinal) ||
                !block.TryGetProperty("text", out var text) ||
                text.ValueKind is not JsonValueKind.String)
            {
                continue;
            }

            var candidate = text.GetString();
            if (!string.IsNullOrWhiteSpace(candidate))
            {
                return candidate;
            }
        }

        return null;
    }

    private async Task DelayBeforeRetryAsync(
        string toolName,
        int attempt,
        string reason,
        CancellationToken cancellationToken)
    {
        var delay = TimeSpan.FromMilliseconds(
            _options.BaseDelayMilliseconds * Math.Pow(2, attempt - 1));
        _logger.LogWarning(
            "Retrying Foundry tool {ToolName} after {Reason}. Attempt {Attempt} of {MaxAttempts}; delay {DelayMs} ms",
            toolName,
            reason,
            attempt,
            _options.MaxAttempts,
            delay.TotalMilliseconds);
        await Task.Delay(delay, cancellationToken);
    }

    private static bool IsTransient(HttpStatusCode statusCode) =>
        statusCode is HttpStatusCode.RequestTimeout
            or HttpStatusCode.TooManyRequests
            or HttpStatusCode.InternalServerError
            or HttpStatusCode.BadGateway
            or HttpStatusCode.ServiceUnavailable
            or HttpStatusCode.GatewayTimeout;

    private static bool IsTransient(int statusCode) =>
        IsTransient((HttpStatusCode)statusCode);

    // Despite returning a "result", this is an error path: no endpoint is
    // configured for the tool, so nothing was invoked. It is named for the
    // condition rather than as a "fallback" so it cannot be mistaken for a
    // successful degraded response.
    private static McpToolResult CreateMissingEndpointErrorResult(string toolName, IDictionary<string, object?> parameters, string? endpoint)
    {
        return new McpToolResult(
            toolName,
            "error",
            "No endpoint is configured for the requested agent tool.",
            new Dictionary<string, object?>
            {
                ["tool_name"] = toolName,
                ["parameters"] = parameters,
                ["endpoint"] = endpoint,
                ["hint"] = "Set FOUNDRY_AGENT_ENDPOINT (or FOUNDRY_TOOL_ENDPOINTS for tool-specific routing) to a Foundry endpoint that accepts authenticated POST requests to enable real agent execution."
            });
    }

    private McpToolResult CreateInvocationErrorResult(
        string toolName,
        string endpoint,
        Exception exception,
        string errorCode,
        int attempts)
    {
        _logger.LogError(
            "Foundry adapter invocation failed for {ToolName} with {ErrorType} after {Attempts} attempt(s)",
            toolName,
            exception.GetType().Name,
            attempts);
        return new McpToolResult(toolName, "error", "Agent invocation failed.", new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["agent_name"] = ResolveAgentName(toolName),
            ["error_code"] = errorCode,
            ["error_type"] = exception.GetType().Name,
            ["attempts"] = attempts
        });
    }

    private sealed class McpProtocolException(int code, string message) : Exception(message)
    {
        public int Code { get; } = code;
    }
}
