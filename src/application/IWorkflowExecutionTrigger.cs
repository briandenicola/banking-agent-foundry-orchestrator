namespace BankingAgent.Application;

/// <summary>
/// Best-effort trigger for immediate workflow execution pickup.
/// Implementations must be safe to call from any context; the trigger
/// fires asynchronously and never blocks the caller.
/// The periodic recovery worker remains the authoritative execution
/// guarantee — this trigger is a latency optimisation only.
/// </summary>
public interface IWorkflowExecutionTrigger
{
    /// <summary>
    /// Signals the recovery mechanism to attempt an immediate claim/execute
    /// cycle for the specified workflow. Returns immediately; execution happens
    /// in the background.
    /// </summary>
    void TriggerImmediate(Guid workflowId);
}
