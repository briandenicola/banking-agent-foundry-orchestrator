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
