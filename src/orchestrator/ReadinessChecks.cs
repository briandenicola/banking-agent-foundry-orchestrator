using System.Text.Json;
using BankingAgent.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace BankingAgent.Orchestrator;

/// <summary>
/// Caches the outcome of live MCP tool validation so readiness probes never
/// perform network I/O inline.
/// </summary>
/// <remarks>
/// A live validation round trip against a Foundry Hosted Agent costs several
/// seconds, while the readiness probe allows five and repeats every ten. Doing
/// the call inline meant the probe could never pass, and that every probe cycle
/// issued another pair of requests to the agent for as long as the app ran.
/// Refresh therefore happens in the background on an independent timeout, and
/// probes read the last known verdict.
/// </remarks>
public sealed class McpToolValidationCache(
    IMcpClient mcpClient,
    ILogger<McpToolValidationCache> logger)
{
    private static readonly TimeSpan SuccessTtl = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan FailureTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan ValidationTimeout = TimeSpan.FromSeconds(60);

    private readonly SemaphoreSlim _refreshGate = new(1, 1);
    private Verdict? _verdict;

    public sealed record Verdict(bool IsHealthy, string? Error, DateTimeOffset ExpiresAt);

    /// <summary>
    /// Returns the cached verdict, starting a background refresh when it is
    /// missing or stale. Never blocks on the network.
    /// </summary>
    public Verdict? GetVerdictAndRefreshIfStale(IReadOnlyCollection<McpRequiredTool> requiredTools)
    {
        var current = Volatile.Read(ref _verdict);

        if (current is null || current.ExpiresAt <= DateTimeOffset.UtcNow)
        {
            _ = RefreshAsync(requiredTools);
        }

        return current;
    }

    private async Task RefreshAsync(IReadOnlyCollection<McpRequiredTool> requiredTools)
    {
        // A single refresh at a time; concurrent probes must not multiply load
        // on the agent.
        if (!await _refreshGate.WaitAsync(TimeSpan.Zero))
        {
            return;
        }

        try
        {
            // Deliberately not the probe's token: the probe is cancelled after
            // five seconds, which is shorter than a healthy round trip.
            using var cancellation = new CancellationTokenSource(ValidationTimeout);

            await mcpClient.ValidateRequiredToolsAsync(requiredTools, cancellation.Token);

            Volatile.Write(
                ref _verdict,
                new Verdict(true, null, DateTimeOffset.UtcNow.Add(SuccessTtl)));
        }
        catch (Exception exception)
        {
            logger.LogWarning(
                exception,
                "MCP tool validation failed; readiness will report unhealthy until it succeeds.");

            Volatile.Write(
                ref _verdict,
                new Verdict(false, exception.Message, DateTimeOffset.UtcNow.Add(FailureTtl)));
        }
        finally
        {
            _refreshGate.Release();
        }
    }
}

public sealed class ServiceAuthReadinessCheck(bool serviceAuthEnabled) : IHealthCheck
{
    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default) =>
        Task.FromResult(serviceAuthEnabled
            ? HealthCheckResult.Healthy("Workflow service authentication is enabled.")
            : HealthCheckResult.Degraded(
                "INSECURE LOCAL DEVELOPMENT CONFIGURATION: workflow endpoints accept unauthenticated callers."));
}

public sealed class PostgreSqlReadinessCheck(NpgsqlDataSource dataSource) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(3));
        try
        {
            await using var connection = await dataSource.OpenConnectionAsync(timeout.Token);
            return HealthCheckResult.Healthy();
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("PostgreSQL readiness check timed out.");
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "PostgreSQL is unavailable.",
                exception);
        }
    }
}

public sealed class FoundryConfigurationReadinessCheck(
    IConfiguration configuration,
    McpToolValidationCache mcpValidationCache) : IHealthCheck
{
    private static readonly string[] RequiredTools =
    [
        "workflow.plan",
        "transaction.explain",
        "suspicious.assess",
        "dispute.plan"
    ];

    private static readonly string[] RequiredToolInputProperties =
    [
        "user_message",
        "trace_id",
        "workflow_id"
    ];

    private static readonly McpRequiredTool[] RequiredMcpTools =
        [.. RequiredTools.Select(tool => new McpRequiredTool(tool, RequiredToolInputProperties))];

    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var endpointMap = configuration["FOUNDRY_TOOL_ENDPOINTS"];
        var mcpEndpointMap = configuration["FOUNDRY_MCP_TOOL_ENDPOINTS"];
        if (string.IsNullOrWhiteSpace(endpointMap))
        {
            return HealthCheckResult.Unhealthy("Foundry tool endpoints are not configured.");
        }

        try
        {
            var endpoints = JsonSerializer.Deserialize<Dictionary<string, string>>(endpointMap);
            var mcpEndpoints = string.IsNullOrWhiteSpace(mcpEndpointMap)
                ? new Dictionary<string, string>()
                : JsonSerializer.Deserialize<Dictionary<string, string>>(mcpEndpointMap);
            var missing = RequiredTools.Where(tool =>
                !TryGetConfiguredEndpoint(tool, endpoints, mcpEndpoints, out var endpoint) ||
                !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttps &&
                 endpointUri.Scheme != Uri.UriSchemeHttp));
            var missingTools = missing.ToArray();
            if (missingTools.Length > 0)
            {
                return HealthCheckResult.Unhealthy(
                    $"Foundry endpoint configuration is invalid for: {string.Join(", ", missingTools)}.");
            }

            var enabledRequiredMcpTools = RequiredMcpTools
                .Where(tool => mcpEndpoints?.ContainsKey(tool.Name) == true)
                .ToArray();
            if (enabledRequiredMcpTools.Length > 0)
            {
                var verdict = mcpValidationCache.GetVerdictAndRefreshIfStale(enabledRequiredMcpTools);

                if (verdict is null)
                {
                    return HealthCheckResult.Unhealthy(
                        "MCP tool validation has not completed yet.");
                }

                if (!verdict.IsHealthy)
                {
                    return HealthCheckResult.Unhealthy(
                        $"Foundry MCP readiness validation failed: {verdict.Error}");
                }
            }

            return HealthCheckResult.Healthy();
        }
        catch (JsonException exception)
        {
            return HealthCheckResult.Unhealthy(
                "Foundry tool endpoint configuration is invalid JSON.",
                exception);
        }
        catch (Exception exception)
        {
            return HealthCheckResult.Unhealthy(
                "Foundry MCP readiness validation failed.",
                exception);
        }
    }

    private static bool TryGetConfiguredEndpoint(
        string toolName,
        IReadOnlyDictionary<string, string>? legacyEndpoints,
        IReadOnlyDictionary<string, string>? mcpEndpoints,
        out string endpoint)
    {
        if (mcpEndpoints is not null &&
            mcpEndpoints.TryGetValue(toolName, out endpoint!))
        {
            return true;
        }

        if (legacyEndpoints is not null &&
            legacyEndpoints.TryGetValue(toolName, out endpoint!))
        {
            return true;
        }

        endpoint = string.Empty;
        return false;
    }
}
