using BankingAgent.Domain;

namespace BankingAgent.Application;

public interface IWorkflowService
{
    Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default);
}

public sealed class WorkflowService : IWorkflowService
{
    private readonly List<WorkflowState> _states = new();

    public Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var workflow = new WorkflowState(
            Id: Guid.NewGuid(),
            TraceId: traceId,
            UserMessage: userMessage,
            Status: WorkflowStatus.Draft,
            Intent: null,
            RequiresApproval: false,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Events: new List<WorkflowEvent>
            {
                new("workflow.started", "Workflow started", DateTimeOffset.UtcNow, "system")
            });

        _states.Add(workflow);
        return Task.FromResult(workflow);
    }

    public Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default)
    {
        var workflow = _states.FirstOrDefault(x => x.Id == workflowId);
        if (workflow is null)
        {
            throw new InvalidOperationException($"Workflow {workflowId} was not found.");
        }

        var updated = workflow with
        {
            Status = decision.Equals("approve", StringComparison.OrdinalIgnoreCase) ? WorkflowStatus.Approved : WorkflowStatus.Rejected,
            ApprovalDecision = decision,
            ApprovalReason = reason,
            UpdatedAt = DateTimeOffset.UtcNow,
            Events = workflow.Events.Append(new WorkflowEvent("workflow.approval", $"Approval {decision}", DateTimeOffset.UtcNow, "user", reason)).ToList()
        };

        _states.Remove(workflow);
        _states.Add(updated);
        return Task.FromResult(updated);
    }
}
