using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.Extensions.Options;

namespace BankingAgent.Orchestrator;

public sealed class WorkflowRecoveryOptions
{
    public int ScanIntervalSeconds { get; set; } = 30;
    public int StaleAfterSeconds { get; set; } = 120;
    public int BatchSize { get; set; } = 10;
}

public sealed class WorkflowRecoveryWorker(
    IServiceScopeFactory scopeFactory,
    IOptions<WorkflowRecoveryOptions> options,
    ILogger<WorkflowRecoveryWorker> logger) : BackgroundService
{
    private readonly WorkflowRecoveryOptions _options = Validate(options.Value);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(
            TimeSpan.FromSeconds(_options.ScanIntervalSeconds));
        do
        {
            try
            {
                await RecoverBatchAsync(stoppingToken);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Workflow recovery scan failed with {ErrorType}",
                    exception.GetType().Name);
            }
        }
        while (await timer.WaitForNextTickAsync(stoppingToken));
    }

    internal async Task RecoverBatchAsync(CancellationToken cancellationToken)
    {
        for (var index = 0; index < _options.BatchSize; index++)
        {
            await using var scope = scopeFactory.CreateAsyncScope();
            var recoveryRepository =
                scope.ServiceProvider.GetRequiredService<IWorkflowRecoveryRepository>();
            var workflowService =
                scope.ServiceProvider.GetRequiredService<IWorkflowService>();
            var claimed = await recoveryRepository.ClaimNextAsync(
                DateTimeOffset.UtcNow.AddSeconds(-_options.StaleAfterSeconds),
                DateTimeOffset.UtcNow,
                cancellationToken);
            if (claimed is null)
            {
                return;
            }

            try
            {
                var recovered = await workflowService.RecoverAsync(
                    claimed.Id,
                    cancellationToken);
                logger.LogInformation(
                    "Recovered workflow {WorkflowId} from version {ClaimedVersion} to status {Status}",
                    claimed.Id,
                    claimed.Version,
                    recovered.Status);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                logger.LogError(
                    "Workflow recovery failed for {WorkflowId} with {ErrorType}",
                    claimed.Id,
                    exception.GetType().Name);
            }
        }
    }

    private static WorkflowRecoveryOptions Validate(WorkflowRecoveryOptions options)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(options.ScanIntervalSeconds, 5);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.ScanIntervalSeconds, 300);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.StaleAfterSeconds, 60);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.StaleAfterSeconds, 3600);
        ArgumentOutOfRangeException.ThrowIfLessThan(options.BatchSize, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(options.BatchSize, 100);
        return options;
    }
}
