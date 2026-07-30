using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Application;

public interface IWorkflowService
{
    Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default);
}

public sealed class WorkflowService : IWorkflowService
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static readonly IReadOnlyDictionary<string, string> SpecialistTools =
        new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["transaction-explanation"] = "transaction.explain",
            ["suspicious-activity"] = "suspicious.assess",
            ["dispute-planning"] = "dispute.plan"
        };

    private readonly IMcpClient _mcpClient;
    private readonly ILogger<WorkflowService> _logger;
    private readonly ConcurrentDictionary<Guid, WorkflowState> _states = new();

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

        var parameters = new Dictionary<string, object?>
        {
            ["user_message"] = userMessage,
            ["trace_id"] = traceId,
            ["workflow_id"] = workflowId.ToString(),
            ["workflow_status"] = "planning"
        };

        var plannerResult = await _mcpClient.InvokeAsync("workflow.plan", parameters, cancellationToken);
        events.Add(CreateInvocationEvent(plannerResult));

        if (!TryReadAgentResult(plannerResult, "workflow-planning", out var plannerDecision, out var plannerError))
        {
            return StoreFailedWorkflow(
                workflowId,
                traceId,
                userMessage,
                createdAt,
                events,
                plannerError);
        }

        if (string.IsNullOrWhiteSpace(plannerDecision.SelectedAgent) ||
            !SpecialistTools.TryGetValue(plannerDecision.SelectedAgent, out var specialistTool))
        {
            return StoreFailedWorkflow(
                workflowId,
                traceId,
                userMessage,
                createdAt,
                events,
                $"Planner selected unsupported agent '{plannerDecision.SelectedAgent ?? "none"}'.");
        }

        var specialistParameters = new Dictionary<string, object?>(parameters)
        {
            ["workflow_status"] = "specialist_processing",
            ["intent"] = plannerDecision.Intent,
            ["context"] = new Dictionary<string, object?>
            {
                ["planner_summary"] = plannerDecision.Summary,
                ["planner_evidence"] = plannerDecision.Evidence,
                ["selected_agent"] = plannerDecision.SelectedAgent
            }
        };

        var specialistResult = await _mcpClient.InvokeAsync(specialistTool, specialistParameters, cancellationToken);
        events.Add(CreateInvocationEvent(specialistResult));

        if (!TryReadAgentResult(
                specialistResult,
                plannerDecision.SelectedAgent,
                out var specialistDecision,
                out var specialistError))
        {
            return StoreFailedWorkflow(
                workflowId,
                traceId,
                userMessage,
                createdAt,
                events,
                specialistError);
        }

        var requiresApproval = plannerDecision.RequiresApproval || specialistDecision.RequiresApproval;
        var status = requiresApproval
            ? WorkflowStatus.WaitingForApproval
            : WorkflowStatus.Completed;
        var updatedAt = DateTimeOffset.UtcNow;

        events.Add(new WorkflowEvent(
            requiresApproval ? "workflow.approval_required" : "workflow.completed",
            requiresApproval ? "Workflow requires explicit approval" : "Workflow completed without approval",
            updatedAt,
            "system",
            specialistDecision.Summary));

        var workflow = new WorkflowState(
            Id: workflowId,
            TraceId: traceId,
            UserMessage: userMessage,
            Status: status,
            Intent: specialistDecision.Intent,
            RequiresApproval: requiresApproval,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            Events: events);

        _states[workflow.Id] = workflow;
        _logger.LogInformation(
            "Workflow {WorkflowId} completed routing for trace {TraceId} with status {Status} and specialist {Specialist}",
            workflow.Id,
            workflow.TraceId,
            workflow.Status,
            plannerDecision.SelectedAgent);
        return workflow;
    }

    public Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default)
    {
        if (!_states.TryGetValue(workflowId, out var workflow))
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

        _states[updated.Id] = updated;
        _logger.LogInformation("Workflow {WorkflowId} was {Decision}d", workflow.Id, decision);
        return Task.FromResult(updated);
    }

    private static WorkflowEvent CreateInvocationEvent(McpToolResult result) =>
        new(
            "mcp.invoked",
            $"Invoked tool {result.ToolName}",
            DateTimeOffset.UtcNow,
            "system",
            $"{result.Status}: {result.Message}");

    private static bool TryReadAgentResult(
        McpToolResult result,
        string expectedAgent,
        out AgentDecision decision,
        out string error)
    {
        decision = default!;

        if (!result.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
        {
            error = $"Tool {result.ToolName} failed with status '{result.Status}': {result.Message}";
            return false;
        }

        if (!result.Data.TryGetValue("response_body", out var responseBodyValue) ||
            responseBodyValue is not string responseBody ||
            string.IsNullOrWhiteSpace(responseBody))
        {
            error = $"Tool {result.ToolName} did not return an agent response body.";
            return false;
        }

        try
        {
            var parsed = JsonSerializer.Deserialize<AgentDecision>(responseBody, JsonOptions);
            if (parsed is null ||
                !string.Equals(parsed.Status, "ok", StringComparison.OrdinalIgnoreCase) ||
                string.IsNullOrWhiteSpace(parsed.Intent) ||
                string.IsNullOrWhiteSpace(parsed.Summary))
            {
                error = $"Tool {result.ToolName} returned an invalid agent result.";
                return false;
            }

            if (!string.Equals(parsed.Agent, expectedAgent, StringComparison.OrdinalIgnoreCase))
            {
                error = $"Tool {result.ToolName} returned agent '{parsed.Agent}' instead of '{expectedAgent}'.";
                return false;
            }

            decision = parsed;
            error = string.Empty;
            return true;
        }
        catch (JsonException ex)
        {
            error = $"Tool {result.ToolName} returned malformed JSON: {ex.Message}";
            return false;
        }
    }

    private WorkflowState StoreFailedWorkflow(
        Guid workflowId,
        string traceId,
        string userMessage,
        DateTimeOffset createdAt,
        List<WorkflowEvent> events,
        string error)
    {
        var updatedAt = DateTimeOffset.UtcNow;
        events.Add(new WorkflowEvent("workflow.failed", "Workflow routing failed", updatedAt, "system", error));

        var workflow = new WorkflowState(
            Id: workflowId,
            TraceId: traceId,
            UserMessage: userMessage,
            Status: WorkflowStatus.Failed,
            Intent: null,
            RequiresApproval: false,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: createdAt,
            UpdatedAt: updatedAt,
            Events: events);

        _states[workflow.Id] = workflow;
        _logger.LogError(
            "Workflow {WorkflowId} failed routing for trace {TraceId}: {Error}",
            workflowId,
            traceId,
            error);
        return workflow;
    }

    private sealed record AgentDecision(
        string Agent,
        string Status,
        string Intent,
        string Summary,
        [property: JsonPropertyName("requires_approval")] bool RequiresApproval,
        [property: JsonPropertyName("selected_agent")] string? SelectedAgent,
        IReadOnlyList<string>? Evidence);
}
