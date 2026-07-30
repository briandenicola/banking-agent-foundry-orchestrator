using System.Text.Json;
using System.Text.Json.Serialization;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Application;

public interface IWorkflowService
{
    Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<WorkflowState> ApproveAsync(Guid workflowId, string decision, string reason, CancellationToken cancellationToken = default);
    Task<WorkflowState?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default);
    Task<SupportCase?> GetSupportCaseAsync(Guid workflowId, CancellationToken cancellationToken = default);
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
    private readonly IWorkflowRepository _workflowRepository;
    private readonly IWorkflowActionRepository _workflowActionRepository;

    public WorkflowService(
        IMcpClient mcpClient,
        ILogger<WorkflowService> logger,
        IWorkflowRepository workflowRepository,
        IWorkflowActionRepository workflowActionRepository)
    {
        _mcpClient = mcpClient;
        _logger = logger;
        _workflowRepository = workflowRepository;
        _workflowActionRepository = workflowActionRepository;
    }

    public async Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var workflowId = Guid.NewGuid();
        var createdAt = DateTimeOffset.UtcNow;

        // Phase 1 — persist draft before invoking any agent.
        var draftState = new WorkflowState(
            Id: workflowId,
            TraceId: traceId,
            UserMessage: userMessage,
            Status: WorkflowStatus.Draft,
            Intent: null,
            RequiresApproval: false,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: createdAt,
            UpdatedAt: createdAt,
            Events: [new WorkflowEvent("workflow.started", "Workflow started", createdAt, "system")],
            Version: 0);

        await _workflowRepository.AddAsync(draftState, cancellationToken);
        var current = draftState;

        // Phase 2 — planner agent.
        var plannerParameters = new Dictionary<string, object?>
        {
            ["user_message"] = userMessage,
            ["trace_id"] = traceId,
            ["workflow_id"] = workflowId.ToString(),
            ["workflow_status"] = "planning"
        };

        McpToolResult plannerResult;
        try
        {
            plannerResult = await _mcpClient.InvokeAsync(
                "workflow.plan",
                plannerParameters,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PersistFailedAsync(
                current,
                [],
                "Planner invocation was canceled.",
                CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            _logger.LogError(
                ex,
                "Planner invocation failed for workflow {WorkflowId}",
                workflowId);
            return await PersistFailedAsync(
                current,
                [],
                "Planner invocation failed.",
                CancellationToken.None);
        }
        var plannerEvent = CreateInvocationEvent(plannerResult);

        if (!TryReadAgentResult(plannerResult, "workflow-planning", out var plannerDecision, out var plannerError))
            return await PersistFailedAsync(current, [plannerEvent], plannerError, cancellationToken);

        current = await AdvanceAndPersistAsync(current, [plannerEvent], cancellationToken);

        // Phase 3 — routing.
        var route = WorkflowRoutingPolicy.Decide(userMessage);

        if (!SpecialistTools.TryGetValue(route.Agent, out var specialistTool))
        {
            return await PersistFailedAsync(
                current,
                [],
                $"Routing policy selected unsupported agent '{route.Agent}'.",
                cancellationToken);
        }

        if (!string.Equals(plannerDecision.SelectedAgent, route.Agent, StringComparison.OrdinalIgnoreCase) ||
            plannerDecision.RequiresApproval != route.RequiresApproval)
        {
            _logger.LogWarning(
                "Workflow {WorkflowId} routing policy overrode planner route {PlannerAgent}/{PlannerApproval} with {PolicyAgent}/{PolicyApproval}",
                workflowId,
                plannerDecision.SelectedAgent,
                plannerDecision.RequiresApproval,
                route.Agent,
                route.RequiresApproval);
        }

        var routeEvent = new WorkflowEvent(
            "workflow.route_selected",
            $"Selected specialist {route.Agent}",
            DateTimeOffset.UtcNow,
            "system",
            $"Approval required: {route.RequiresApproval}");

        current = await AdvanceAndPersistAsync(current, [routeEvent], cancellationToken);

        // Phase 4 — specialist agent.
        var specialistParameters = new Dictionary<string, object?>(plannerParameters)
        {
            ["workflow_status"] = "specialist_processing",
            ["intent"] = plannerDecision.Intent,
            ["context"] = new Dictionary<string, object?>
            {
                ["planner_summary"] = plannerDecision.Summary,
                ["planner_evidence"] = plannerDecision.Evidence,
                ["planner_selected_agent"] = plannerDecision.SelectedAgent,
                ["selected_agent"] = route.Agent
            }
        };

        McpToolResult specialistResult;
        try
        {
            specialistResult = await _mcpClient.InvokeAsync(
                specialistTool,
                specialistParameters,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PersistFailedAsync(
                current,
                [],
                "Specialist invocation was canceled.",
                CancellationToken.None);
            throw;
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            _logger.LogError(
                ex,
                "Specialist invocation failed for workflow {WorkflowId}",
                workflowId);
            return await PersistFailedAsync(
                current,
                [],
                "Specialist invocation failed.",
                CancellationToken.None);
        }
        var specialistEvent = CreateInvocationEvent(specialistResult);

        if (!TryReadAgentResult(specialistResult, route.Agent, out var specialistDecision, out var specialistError))
            return await PersistFailedAsync(current, [specialistEvent], specialistError, cancellationToken);

        // Phase 5 — terminal state.
        var requiresApproval = route.RequiresApproval;
        var terminalStatus = requiresApproval ? WorkflowStatus.WaitingForApproval : WorkflowStatus.Completed;
        var now = DateTimeOffset.UtcNow;
        var terminalEvent = new WorkflowEvent(
            requiresApproval ? "workflow.approval_required" : "workflow.completed",
            requiresApproval ? "Workflow requires explicit approval" : "Workflow completed without approval",
            now,
            "system",
            specialistDecision.Summary);

        var finalEvents = current.Events.Concat([specialistEvent, terminalEvent]).ToList();
        var finalState = current with
        {
            Status = terminalStatus,
            Intent = specialistDecision.Intent,
            RequiresApproval = requiresApproval,
            UpdatedAt = now,
            Events = finalEvents,
            Version = current.Version + 1
        };

        await _workflowRepository.UpdateAsync(finalState, current.Version, cancellationToken);

        _logger.LogInformation(
            "Workflow {WorkflowId} completed routing for trace {TraceId} with status {Status} and specialist {Specialist}",
            workflowId, traceId, finalState.Status, route.Agent);

        return finalState;
    }

    public async Task<WorkflowState> ApproveAsync(
        Guid workflowId,
        string decision,
        string reason,
        CancellationToken cancellationToken = default)
    {
        var current = await _workflowRepository.GetAsync(workflowId, cancellationToken)
            ?? throw new WorkflowNotFoundException(workflowId);

        // Idempotency — return immediately if the same decision is already recorded.
        if (current.ApprovalDecision is not null)
        {
            if (string.Equals(current.ApprovalDecision, decision, StringComparison.OrdinalIgnoreCase))
                return current;

            throw new ConflictingDecisionException(workflowId, current.ApprovalDecision, decision);
        }

        if (current.Status != WorkflowStatus.WaitingForApproval)
            throw new InvalidTransitionException(workflowId, current.Status, WorkflowStatus.WaitingForApproval.ToString());

        var isApproved = decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
        var finalStatus = isApproved ? WorkflowStatus.Completed : WorkflowStatus.Rejected;
        var now = DateTimeOffset.UtcNow;
        var approvalEvent = new WorkflowEvent("workflow.approval", $"Approval {decision}", now, "user", reason);

        var newState = current with
        {
            Status = finalStatus,
            ApprovalDecision = decision,
            ApprovalReason = reason,
            UpdatedAt = now,
            Events = current.Events.Append(approvalEvent).ToList(),
            Version = current.Version + 1
        };

        var approvalRecord = new ApprovalDecision(
            Id: Guid.NewGuid(),
            WorkflowId: workflowId,
            Decision: decision,
            Reason: reason,
            Actor: "user",
            CreatedAt: now);

        await _workflowActionRepository.RecordDecisionAsync(
            newState,
            approvalRecord,
            actionExecution: null,
            supportCase: null,
            expectedVersion: current.Version,
            cancellationToken);

        _logger.LogInformation("Workflow {WorkflowId} decision recorded: {Decision}", workflowId, decision);
        return newState;
    }

    public Task<WorkflowState?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => _workflowRepository.GetAsync(workflowId, cancellationToken);

    public Task<SupportCase?> GetSupportCaseAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => _workflowActionRepository.GetSupportCaseAsync(workflowId, cancellationToken);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private async Task<WorkflowState> AdvanceAndPersistAsync(
        WorkflowState current,
        WorkflowEvent[] newEvents,
        CancellationToken cancellationToken)
    {
        var next = current with
        {
            Events = current.Events.Concat(newEvents).ToList(),
            UpdatedAt = DateTimeOffset.UtcNow,
            Version = current.Version + 1
        };
        await _workflowRepository.UpdateAsync(next, current.Version, cancellationToken);
        return next;
    }

    private async Task<WorkflowState> PersistFailedAsync(
        WorkflowState current,
        WorkflowEvent[] additionalEvents,
        string error,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;
        var failEvent = new WorkflowEvent("workflow.failed", "Workflow routing failed", now, "system", error);
        var allEvents = current.Events.Concat(additionalEvents).Append(failEvent).ToList();

        var failedState = current with
        {
            Status = WorkflowStatus.Failed,
            UpdatedAt = now,
            Events = allEvents,
            Version = current.Version + 1
        };

        await _workflowRepository.UpdateAsync(failedState, current.Version, cancellationToken);

        _logger.LogError(
            "Workflow {WorkflowId} failed for trace {TraceId}: {Error}",
            current.Id, current.TraceId, error);

        return failedState;
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

    private sealed record AgentDecision(
        string Agent,
        string Status,
        string Intent,
        string Summary,
        [property: JsonPropertyName("requires_approval")] bool RequiresApproval,
        [property: JsonPropertyName("selected_agent")] string? SelectedAgent,
        IReadOnlyList<string>? Evidence);
}
