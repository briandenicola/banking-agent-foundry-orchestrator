using System.Text.Json;
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

public sealed class FoundryConfigurationReadinessCheck(IConfiguration configuration) : IHealthCheck
{
    private static readonly string[] RequiredTools =
    [
        "workflow.plan",
        "transaction.explain",
        "suspicious.assess",
        "dispute.plan"
    ];

    public Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        var endpointMap = configuration["FOUNDRY_TOOL_ENDPOINTS"];
        if (string.IsNullOrWhiteSpace(endpointMap))
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy("Foundry tool endpoints are not configured."));
        }

        try
        {
            var endpoints = JsonSerializer.Deserialize<Dictionary<string, string>>(endpointMap);
            var missing = RequiredTools.Where(tool =>
                endpoints is null ||
                !endpoints.TryGetValue(tool, out var endpoint) ||
                !Uri.TryCreate(endpoint, UriKind.Absolute, out var endpointUri) ||
                (endpointUri.Scheme != Uri.UriSchemeHttps &&
                 endpointUri.Scheme != Uri.UriSchemeHttp));
            var missingTools = missing.ToArray();
            return Task.FromResult(missingTools.Length == 0
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Foundry endpoint configuration is invalid for: {string.Join(", ", missingTools)}."));
        }
        catch (JsonException exception)
        {
            return Task.FromResult(
                HealthCheckResult.Unhealthy(
                    "Foundry tool endpoint configuration is invalid JSON.",
                    exception));
        }
    }
}
