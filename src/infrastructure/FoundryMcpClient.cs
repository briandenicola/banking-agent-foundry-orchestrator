using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
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
    public string Scope { get; set; } = "https://ai.azure.com/.default";
    public string? ToolEndpointsJson { get; set; }
    public int MaxAttempts { get; set; } = 3;
    public int AttemptTimeoutSeconds { get; set; } = 30;
    public int BaseDelayMilliseconds { get; set; } = 250;
}

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
    private readonly FoundryMcpClientOptions _options;

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
        _scope = string.IsNullOrWhiteSpace(_options.Scope)
            ? ReadSetting("FOUNDRY_SCOPE") ?? "https://ai.azure.com/.default"
            : _options.Scope;
        _toolEndpoints = ReadToolEndpointMap(
            _options.ToolEndpointsJson ?? ReadSetting("FOUNDRY_TOOL_ENDPOINTS"));
    }

    public async Task<McpToolResult> InvokeAsync(string toolName, IDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var endpoint = ResolveEndpoint(toolName);
        if (string.IsNullOrWhiteSpace(endpoint))
        {
            _logger.LogWarning("No Foundry endpoint configured for {ToolName}; returning a local fallback response", toolName);
            return CreateFallbackResult(toolName, parameters, endpoint);
        }

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
                if (parameters.TryGetValue("correlation_id", out var correlationId) &&
                    correlationId is not null)
                {
                    request.Headers.TryAddWithoutValidation(
                        "x-correlation-id",
                        correlationId.ToString());
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
            ["attempts"] = attempt
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
}
