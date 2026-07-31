using Microsoft.Extensions.Diagnostics.HealthChecks;

namespace BankingAgent.WebUi;

public sealed class OrchestratorReadinessCheck(
    IHttpClientFactory httpClientFactory) : IHealthCheck
{
    public async Task<HealthCheckResult> CheckHealthAsync(
        HealthCheckContext context,
        CancellationToken cancellationToken = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            using var response = await httpClientFactory
                .CreateClient("orchestrator-health")
                .GetAsync("/health/ready", timeout.Token);
            return response.IsSuccessStatusCode
                ? HealthCheckResult.Healthy()
                : HealthCheckResult.Unhealthy(
                    $"Orchestrator readiness returned HTTP {(int)response.StatusCode}.");
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return HealthCheckResult.Unhealthy("Orchestrator readiness check timed out.");
        }
        catch (HttpRequestException exception)
        {
            return HealthCheckResult.Unhealthy(
                "Orchestrator readiness endpoint is unavailable.",
                exception);
        }
    }
}
