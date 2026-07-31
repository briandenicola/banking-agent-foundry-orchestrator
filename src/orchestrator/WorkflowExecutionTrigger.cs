using BankingAgent.Application;
using BankingAgent.Domain;

namespace BankingAgent.Orchestrator;

/// <summary>
/// Singleton trigger that creates a fresh DI scope and claims+executes
/// the specified Draft workflow immediately.
/// Safe: owns its own IServiceScopeFactory reference; never captures scoped
/// services from the triggering request. All exceptions are swallowed —
/// the periodic WorkflowRecoveryWorker guarantees delivery.
/// </summary>
public sealed class WorkflowExecutionTrigger(
    IServiceScopeFactory scopeFactory,
    ILogger<WorkflowExecutionTrigger> logger) : IWorkflowExecutionTrigger
{
    public void TriggerImmediate(Guid workflowId)
    {
        // Task.Run is deliberate: the trigger must never block the caller.
        // IServiceScopeFactory is a singleton — safe to capture.
        _ = Task.Run(async () =>
        {
            try
            {
                await using var scope = scopeFactory.CreateAsyncScope();
                var recoveryRepository =
                    scope.ServiceProvider.GetRequiredService<IWorkflowRecoveryRepository>();
                var workflowService =
                    scope.ServiceProvider.GetRequiredService<IWorkflowService>();

                var claimed = await recoveryRepository.ClaimAsync(
                    workflowId,
                    DateTimeOffset.UtcNow,
                    CancellationToken.None);

                if (claimed is null)
                {
                    return;
                }

                logger.LogInformation(
                    "Immediate trigger claimed workflow {WorkflowId} at version {Version}",
                    claimed.Id,
                    claimed.Version);

                await workflowService.RecoverAsync(claimed.Id, CancellationToken.None);
            }
            catch (Exception ex)
            {
                logger.LogError(
                    ex,
                    "Immediate workflow execution trigger failed for {WorkflowId} — periodic worker will retry",
                    workflowId);
            }
        });
    }
}
