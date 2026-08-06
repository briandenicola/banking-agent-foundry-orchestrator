using System.Text.Json;
using BankingAgent.Application;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Npgsql;

namespace BankingAgent.Orchestrator;

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
    IMcpClient mcpClient) : IHealthCheck
{
    private static readonly string[] RequiredTools =
    [
        "workflow.plan",
        "transaction.explain",
        "suspicious.assess",
        "dispute.plan"
    ];

    private static readonly McpRequiredTool[] RequiredMcpTools =
    [
        new("transaction.explain", ["user_message", "trace_id", "workflow_id"])
    ];

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
                await mcpClient.ValidateRequiredToolsAsync(enabledRequiredMcpTools, cancellationToken);
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
