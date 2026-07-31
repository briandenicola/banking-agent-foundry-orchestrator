namespace BankingAgent.Domain;

public sealed class WorkflowNotFoundException(Guid workflowId)
    : Exception($"Workflow {workflowId} was not found.")
{
    public Guid WorkflowId { get; } = workflowId;
}

public abstract class WorkflowConflictException(string message) : Exception(message);

public sealed class StaleVersionException(Guid workflowId, long expectedVersion, long actualVersion)
    : WorkflowConflictException(
        $"Workflow {workflowId} was modified concurrently. Expected version {expectedVersion}, found {actualVersion}.")
{
    public Guid WorkflowId { get; } = workflowId;
    public long ExpectedVersion { get; } = expectedVersion;
    public long ActualVersion { get; } = actualVersion;
}

public sealed class ConflictingDecisionException(Guid workflowId, string existingDecision, string newDecision)
    : WorkflowConflictException(
        $"Workflow {workflowId} already has a '{existingDecision}' decision; cannot apply '{newDecision}'.")
{
    public Guid WorkflowId { get; } = workflowId;
    public string ExistingDecision { get; } = existingDecision;
    public string NewDecision { get; } = newDecision;
}

public sealed class InvalidTransitionException(Guid workflowId, WorkflowStatus currentStatus, string requiredStatus)
    : WorkflowConflictException(
        $"Workflow {workflowId} is in '{currentStatus}' status and cannot transition (requires '{requiredStatus}').")
{
    public Guid WorkflowId { get; } = workflowId;
    public WorkflowStatus CurrentStatus { get; } = currentStatus;
    public string RequiredStatus { get; } = requiredStatus;
}

public sealed class EvidenceValidationException(string message) : WorkflowConflictException(message);

public sealed class EvidenceNotAllowedException(Guid workflowId)
    : WorkflowConflictException($"Workflow {workflowId} does not accept dispute evidence.")
{
    public Guid WorkflowId { get; } = workflowId;
}

public sealed class EvidenceNotFoundException(Guid workflowId, Guid evidenceId)
    : Exception($"Evidence {evidenceId} does not exist for workflow {workflowId}.")
{
    public Guid WorkflowId { get; } = workflowId;
    public Guid EvidenceId { get; } = evidenceId;
}
