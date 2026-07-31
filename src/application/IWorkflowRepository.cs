using BankingAgent.Domain;

namespace BankingAgent.Application;

public interface IWorkflowRepository
{
    Task AddAsync(
        WorkflowState workflow,
        CancellationToken cancellationToken = default);

    Task<WorkflowState?> GetAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default);

    Task UpdateAsync(
        WorkflowState workflow,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowActionRepository
{
    Task<SupportCase?> GetSupportCaseAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default);

    Task RecordDecisionAsync(
        WorkflowState workflow,
        ApprovalDecision decision,
        ActionExecution? actionExecution,
        SupportCase? supportCase,
        long expectedVersion,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowRecoveryRepository
{
    Task<WorkflowState?> ClaimAsync(
        Guid workflowId,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default);

    Task<WorkflowState?> ClaimNextAsync(
        DateTimeOffset staleBefore,
        DateTimeOffset claimedAt,
        CancellationToken cancellationToken = default);
}

public interface IWorkflowEvidenceRepository
{
    Task<IReadOnlyList<WorkflowEvidence>> ListAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default);

    Task<WorkflowEvidence?> GetAsync(
        Guid workflowId,
        Guid evidenceId,
        CancellationToken cancellationToken = default);

    Task AddAsync(
        IReadOnlyList<WorkflowEvidence> evidence,
        CancellationToken cancellationToken = default);
}
