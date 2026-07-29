using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure;
using Azure.Core;
using Azure.Identity;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Infrastructure;

public sealed class FoundryMcpClient : IMcpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FoundryMcpClient> _logger;
    private readonly DefaultAzureCredential _credential = new(new DefaultAzureCredentialOptions
    {
        ExcludeVisualStudioCodeCredential = true,
        ExcludeInteractiveBrowserCredential = true
    });

    private readonly string? _defaultEndpoint;
    private readonly string? _agentName;
    private readonly string _scope;
    private readonly IReadOnlyDictionary<string, string> _toolEndpoints;

    public FoundryMcpClient(HttpClient httpClient, ILogger<FoundryMcpClient> logger)
    {
        _httpClient = httpClient;
        _logger = logger;
        _defaultEndpoint = ReadSetting("FOUNDRY_AGENT_ENDPOINT");
        _agentName = ReadSetting("FOUNDRY_AGENT_NAME");
        _scope = ReadSetting("FOUNDRY_SCOPE") ?? "https://ai.azure.com/.default";
        _toolEndpoints = ReadToolEndpointMap(ReadSetting("FOUNDRY_TOOL_ENDPOINTS"));
    }

    public async Task<McpToolResult> InvokeAsync(string toolName, IDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveEndpoint(toolName);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("No Foundry endpoint configured for {ToolName}; returning a local fallback response", toolName);
            return CreateFallbackResult(toolName, parameters, endpoint);
        }

        try
        {
            var traceId = parameters.TryGetValue("trace_id", out var traceIdValue) ? traceIdValue?.ToString() : null;
            var payload = new
            {
                tool_name = toolName,
                agent_name = ResolveAgentName(toolName),
                input = parameters,
                trace_id = traceId,
                message = parameters.TryGetValue("user_message", out var userMessage) ? userMessage?.ToString() : null,
                metadata = new
                {
                    tool_name = toolName,
                    trace_id = traceId,
                    workflow_id = parameters.TryGetValue("workflow_id", out var workflowId) ? workflowId?.ToString() : null
                }
            };

            using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
            {
                Content = JsonContent.Create(payload)
            };

            if (RequiresFoundryToken(endpoint))
            {
                var token = await _credential.GetTokenAsync(new TokenRequestContext(new[] { _scope }), cancellationToken);
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
            }

            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            using var response = await _httpClient.SendAsync(request, cancellationToken);
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            var parsedResponse = TryParseResponse(body);
            var data = new Dictionary<string, object?>
            {
                ["endpoint"] = endpoint,
                ["agent_name"] = ResolveAgentName(toolName),
                ["status_code"] = (int)response.StatusCode,
                ["response_body"] = body,
                ["response_json"] = parsedResponse
            };

            var message = response.IsSuccessStatusCode
                ? "Foundry adapter invocation completed successfully."
                : "Foundry adapter returned an error response.";

            return response.IsSuccessStatusCode
                ? new McpToolResult(toolName, "ok", message, data)
                : new McpToolResult(toolName, "error", message, data);
        }
        catch (TaskCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return CreateInvocationErrorResult(toolName, endpoint, ex);
        }
        catch (HttpRequestException ex)
        {
            return CreateInvocationErrorResult(toolName, endpoint, ex);
        }
        catch (AuthenticationFailedException ex)
        {
            return CreateInvocationErrorResult(toolName, endpoint, ex);
        }
        catch (RequestFailedException ex)
        {
            return CreateInvocationErrorResult(toolName, endpoint, ex);
        }
    }

    private string ResolveEndpoint(string toolName)
    {
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

    private static McpToolResult CreateFallbackResult(string toolName, IDictionary<string, object?> parameters, string? endpoint)
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

    private McpToolResult CreateInvocationErrorResult(string toolName, string endpoint, Exception exception)
    {
        _logger.LogError(exception, "Foundry adapter invocation failed for {ToolName}", toolName);
        return new McpToolResult(toolName, "error", "Agent invocation failed.", new Dictionary<string, object?>
        {
            ["endpoint"] = endpoint,
            ["agent_name"] = ResolveAgentName(toolName),
            ["error"] = exception.Message
        });
    }
}
