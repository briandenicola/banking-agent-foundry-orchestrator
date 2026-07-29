using BankingAgent.Domain;
using BankingAgent.Infrastructure;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Application;

public interface IWorkflowService
{
    Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default);
}

public sealed class WorkflowService : IWorkflowService
{
    private readonly IMcpClient _mcpClient;
    private readonly ILogger<WorkflowService> _logger;
    private readonly List<WorkflowState> _states = new();

    public WorkflowService(IMcpClient mcpClient, ILogger<WorkflowService> logger)
    {
        _mcpClient = mcpClient;
        _logger = logger;
    }

    public async Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var workflowId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        var events = new List<WorkflowEvent>
        {
            new("workflow.started", "Workflow started", createdAt, "system")
        };

        var mcpResult = await _mcpClient.InvokeAsync("workflow.plan", new Dictionary<string, object?>
        {
            ["user_message"] = userMessage,
            ["trace_id"] = traceId
        }, cancellationToken);

        events.Add(new WorkflowEvent("mcp.invoked", $"Invoked tool {mcpResult.ToolName}", DateTimeOffset.UtcNow, "system", mcpResult.Message));

        var workflow = new WorkflowState(
            Id: workflowId,
            TraceId: traceId,
            UserMessage: userMessage,
            Status: WorkflowStatus.WaitingForApproval,
            Intent: null,
            RequiresApproval: true,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: createdAt,
            UpdatedAt: createdAt,
            Events: events);

        _states.Add(workflow);
        _logger.LogInformation("Workflow {WorkflowId} started for trace {TraceId}", workflow.Id, workflow.TraceId);
        return workflow;
    }

    public Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default)
    {
        var workflow = _states.FirstOrDefault(x => x.Id == workflowId);
        if (workflow is null)
        {
            throw new InvalidOperationException($"Workflow {workflowId} was not found.");
        }

        if (workflow.Status != WorkflowStatus.WaitingForApproval)
        {
            throw new InvalidOperationException("Workflow is not awaiting approval.");
        }

        var isApproved = decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
        var finalStatus = isApproved ? WorkflowStatus.Completed : WorkflowStatus.Rejected;
        var updated = workflow with
        {
            Status = finalStatus,
            ApprovalDecision = decision,
            ApprovalReason = reason,
            UpdatedAt = DateTimeOffset.UtcNow,
            Events = workflow.Events.Append(new WorkflowEvent("workflow.approval", $"Approval {decision}", DateTimeOffset.UtcNow, "user", reason)).ToList()
        };

        _states.Remove(workflow);
        _states.Add(updated);
        _logger.LogInformation("Workflow {WorkflowId} was {Decision}d", workflow.Id, decision);
        return Task.FromResult(updated);
    }
}
