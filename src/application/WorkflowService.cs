using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Application;

public interface IWorkflowService
{
    Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<WorkflowState> StartDemoAsync(string scenarioId, CancellationToken cancellationToken = default);
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
    private readonly IDemoScenarioPolicy _demoScenarioPolicy;

    public WorkflowService(
        IMcpClient mcpClient,
        ILogger<WorkflowService> logger,
        IWorkflowRepository workflowRepository,
        IWorkflowActionRepository workflowActionRepository,
        IDemoScenarioPolicy? demoScenarioPolicy = null)
    {
        _mcpClient = mcpClient;
        _logger = logger;
        _workflowRepository = workflowRepository;
        _workflowActionRepository = workflowActionRepository;
        _demoScenarioPolicy = demoScenarioPolicy ?? DemoScenarioPolicy.Disabled;
    }

    public Task<WorkflowState> StartAsync(
        string userMessage,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(userMessage, null, cancellationToken);

    public Task<WorkflowState> StartDemoAsync(
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        var scenario = _demoScenarioPolicy.Resolve(scenarioId);
        return StartCoreAsync(scenario.UserMessage, scenario, cancellationToken);
    }

    private async Task<WorkflowState> StartCoreAsync(
        string userMessage,
        DemoScenarioDefinition? demoScenario,
        CancellationToken cancellationToken)
    {
        var workflowId = Guid.NewGuid();
        using var workflowActivity = WorkflowTelemetry.StartActivity(
            "workflow.lifecycle",
            workflowId);
        var traceId = Activity.Current?.TraceId.ToString() ?? Guid.NewGuid().ToString("N");
        workflowActivity?.SetTag("workflow.trace_id", traceId);
        workflowActivity?.SetTag("workflow.operation", "start");
        workflowActivity?.SetTag("demo.scenario", demoScenario?.Id);
        var startedAt = Stopwatch.GetTimestamp();
        var createdAt = DateTimeOffset.UtcNow;
        var initialEvents = new List<WorkflowEvent>
        {
            new("workflow.started", "Workflow started", createdAt, "system")
        };
        if (demoScenario is not null)
        {
            initialEvents.Add(new WorkflowEvent(
                "workflow.demo_scenario",
                $"Demo scenario: {demoScenario.Title}",
                createdAt,
                "system",
                $"Expected initial status: {demoScenario.ExpectedInitialStatus}; expected decision: {demoScenario.ExpectedDecision ?? "none"}"));
        }

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
            Events: initialEvents,
            Version: 0);

        await AddWorkflowAsync(draftState, cancellationToken);
        var current = draftState;

        // Phase 2 — planner agent.
        var plannerParameters = new Dictionary<string, object?>
        {
            ["user_message"] = userMessage,
            ["trace_id"] = traceId,
            ["workflow_id"] = workflowId.ToString(),
            ["workflow_status"] = "planning",
            ["correlation_id"] = WorkflowTelemetry.GetCorrelationId()
        };

        McpToolResult plannerResult;
        try
        {
            plannerResult = await InvokeAgentAsync(
                "workflow.plan",
                "workflow-planning",
                workflowId,
                traceId,
                plannerParameters,
                DemoScenarioFault.None,
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
            specialistResult = await InvokeAgentAsync(
                specialistTool,
                route.Agent,
                workflowId,
                traceId,
                specialistParameters,
                demoScenario?.Fault ?? DemoScenarioFault.None,
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

        await UpdateWorkflowAsync(finalState, current.Version, cancellationToken);
        WorkflowTelemetry.RecordSuccess(workflowActivity, finalState.Status.ToString());

        _logger.LogInformation(
            "Workflow {WorkflowId} completed routing for trace {TraceId} with status {Status}, specialist {Specialist}, and duration {DurationMs} ms",
            workflowId,
            traceId,
            finalState.Status,
            route.Agent,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);

        return finalState;
    }

    public async Task<WorkflowState> ApproveAsync(
        Guid workflowId,
        string decision,
        string reason,
        CancellationToken cancellationToken = default)
    {
        using var approvalActivity = WorkflowTelemetry.StartActivity(
            "workflow.approval",
            workflowId);
        approvalActivity?.SetTag("approval.decision", decision);
        var startedAt = Stopwatch.GetTimestamp();
        var current = await GetWorkflowAsync(workflowId, cancellationToken);
        if (current is null)
        {
            var exception = new WorkflowNotFoundException(workflowId);
            WorkflowTelemetry.RecordFailure(approvalActivity, exception, "not_found");
            throw exception;
        }

        approvalActivity?.SetTag("workflow.trace_id", current.TraceId);

        // Idempotency — return immediately if the same decision is already recorded.
        if (current.ApprovalDecision is not null)
        {
            if (string.Equals(current.ApprovalDecision, decision, StringComparison.OrdinalIgnoreCase))
            {
                WorkflowTelemetry.RecordSuccess(approvalActivity, "idempotent");
                return current;
            }

            var exception = new ConflictingDecisionException(
                workflowId,
                current.ApprovalDecision,
                decision);
            WorkflowTelemetry.RecordFailure(approvalActivity, exception, "conflict");
            throw exception;
        }

        if (current.Status != WorkflowStatus.WaitingForApproval)
        {
            var exception = new InvalidTransitionException(
                workflowId,
                current.Status,
                WorkflowStatus.WaitingForApproval.ToString());
            WorkflowTelemetry.RecordFailure(approvalActivity, exception, "invalid_transition");
            throw exception;
        }

        var isApproved = decision.Equals("approve", StringComparison.OrdinalIgnoreCase);
        var finalStatus = isApproved ? WorkflowStatus.Completed : WorkflowStatus.Rejected;
        var now = DateTimeOffset.UtcNow;
        var approvalEvent = new WorkflowEvent("workflow.approval", $"Approval {decision}", now, "user", reason);
        var isDispute = string.Equals(
            WorkflowRoutingPolicy.Decide(current.UserMessage).Agent,
            "dispute-planning",
            StringComparison.OrdinalIgnoreCase);
        var supportCase = isApproved && isDispute
            ? CreateSupportCase(current, now)
            : null;
        var actionExecution = supportCase is not null
            ? CreateActionExecution(current, supportCase, now)
            : null;
        var decisionEvents = actionExecution is null
            ? [approvalEvent]
            : new[]
            {
                approvalEvent,
                new WorkflowEvent(
                    "workflow.action_completed",
                    "Simulated dispute support case created",
                    now,
                    "system",
                    supportCase!.CaseNumber)
            };

        var newState = current with
        {
            Status = finalStatus,
            ApprovalDecision = decision,
            ApprovalReason = reason,
            UpdatedAt = now,
            Events = current.Events.Concat(decisionEvents).ToList(),
            Version = current.Version + 1
        };

        var approvalRecord = new ApprovalDecision(
            Id: Guid.NewGuid(),
            WorkflowId: workflowId,
            Decision: decision,
            Reason: reason,
            Actor: "user",
            CreatedAt: now);

        try
        {
            await RecordDecisionAsync(
                newState,
                approvalRecord,
                actionExecution,
                supportCase,
                expectedVersion: current.Version,
                cancellationToken);
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(approvalActivity, ex);
            throw;
        }

        WorkflowTelemetry.RecordSuccess(approvalActivity, finalStatus.ToString());
        _logger.LogInformation(
            "Workflow {WorkflowId} decision {Decision} recorded with outcome {Outcome} in {DurationMs} ms",
            workflowId,
            decision,
            finalStatus,
            Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
        return await GetWorkflowAsync(workflowId, cancellationToken) ?? newState;
    }

    public Task<WorkflowState?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => GetWorkflowAsync(workflowId, cancellationToken);

    public Task<SupportCase?> GetSupportCaseAsync(Guid workflowId, CancellationToken cancellationToken = default)
        => LoadSupportCaseAsync(workflowId, cancellationToken);

    // -------------------------------------------------------------------------
    // Helpers
    // -------------------------------------------------------------------------

    private static SupportCase CreateSupportCase(WorkflowState workflow, DateTimeOffset now) =>
        new(
            Id: Guid.NewGuid(),
            WorkflowId: workflow.Id,
            CaseNumber: $"DSP-{workflow.Id:N}",
            Status: "Open",
            Summary: "Simulated support case for an approved transaction dispute.",
            CreatedAt: now,
            UpdatedAt: now);

    private static ActionExecution CreateActionExecution(
        WorkflowState workflow,
        SupportCase supportCase,
        DateTimeOffset now) =>
        new(
            Id: Guid.NewGuid(),
            WorkflowId: workflow.Id,
            ActionType: "dispute.support_case.create",
            IdempotencyKey: $"dispute-support-case:{workflow.Id:N}",
            Status: ActionExecutionStatus.Completed,
            RequestedAt: now,
            CompletedAt: now,
            Result: JsonSerializer.Serialize(new
            {
                support_case_id = supportCase.Id,
                case_number = supportCase.CaseNumber,
                status = supportCase.Status
            }));

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
        await UpdateWorkflowAsync(next, current.Version, cancellationToken);
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

        await UpdateWorkflowAsync(failedState, current.Version, cancellationToken);
        Activity.Current?.SetTag("workflow.status", WorkflowStatus.Failed.ToString());
        Activity.Current?.SetTag("outcome", "failed");
        Activity.Current?.SetStatus(ActivityStatusCode.Error);

        _logger.LogError(
            "Workflow {WorkflowId} failed for trace {TraceId} with error code {ErrorCode} after {DurationMs} ms",
            current.Id,
            current.TraceId,
            "workflow_execution_failed",
            (now - current.CreatedAt).TotalMilliseconds);

        return failedState;
    }

    private async Task<McpToolResult> InvokeAgentAsync(
        string toolName,
        string agentName,
        Guid workflowId,
        string traceId,
        IDictionary<string, object?> parameters,
        DemoScenarioFault demoFault,
        CancellationToken cancellationToken)
    {
        using var activity = WorkflowTelemetry.StartActivity(
            "hosted_agent.invoke",
            workflowId,
            traceId);
        activity?.SetTag("agent.name", agentName);
        activity?.SetTag("tool.name", toolName);
        var startedAt = Stopwatch.GetTimestamp();

        try
        {
            var result = demoFault switch
            {
                DemoScenarioFault.HostedAgentFailure => new McpToolResult(
                    toolName,
                    "error",
                    "The demo scenario simulated a Hosted Agent failure.",
                    new Dictionary<string, object?>()),
                DemoScenarioFault.HostedAgentTimeout => throw new TimeoutException(
                    "The demo scenario simulated a Hosted Agent timeout."),
                _ => await _mcpClient.InvokeAsync(toolName, parameters, cancellationToken)
            };
            activity?.SetTag("agent.status", result.Status);
            activity?.SetTag("duration_ms", Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            if (result.Status.Equals("ok", StringComparison.OrdinalIgnoreCase))
            {
                WorkflowTelemetry.RecordSuccess(activity);
            }
            else
            {
                activity?.SetTag("outcome", "agent_error");
                activity?.SetStatus(ActivityStatusCode.Error);
            }

            _logger.LogInformation(
                "Hosted Agent {AgentName} completed tool {ToolName} for workflow {WorkflowId} and trace {TraceId} with outcome {Outcome} in {DurationMs} ms",
                agentName,
                toolName,
                workflowId,
                traceId,
                result.Status,
                Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            return result;
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(activity, ex);
            activity?.SetTag("duration_ms", Stopwatch.GetElapsedTime(startedAt).TotalMilliseconds);
            throw;
        }
    }

    private async Task AddWorkflowAsync(
        WorkflowState workflow,
        CancellationToken cancellationToken)
    {
        using var activity = WorkflowTelemetry.StartActivity(
            "persistence.workflow.add",
            workflow.Id,
            workflow.TraceId);
        try
        {
            await _workflowRepository.AddAsync(workflow, cancellationToken);
            WorkflowTelemetry.RecordSuccess(activity);
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(activity, ex);
            throw;
        }
    }

    private async Task<WorkflowState?> GetWorkflowAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        using var activity = WorkflowTelemetry.StartActivity(
            "persistence.workflow.get",
            workflowId);
        try
        {
            var workflow = await _workflowRepository.GetAsync(workflowId, cancellationToken);
            activity?.SetTag("persistence.found", workflow is not null);
            WorkflowTelemetry.RecordSuccess(activity);
            return workflow;
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(activity, ex);
            throw;
        }
    }

    private async Task UpdateWorkflowAsync(
        WorkflowState workflow,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        using var activity = WorkflowTelemetry.StartActivity(
            "persistence.workflow.update",
            workflow.Id,
            workflow.TraceId);
        activity?.SetTag("workflow.expected_version", expectedVersion);
        activity?.SetTag("workflow.version", workflow.Version);
        try
        {
            await _workflowRepository.UpdateAsync(workflow, expectedVersion, cancellationToken);
            WorkflowTelemetry.RecordSuccess(activity);
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(activity, ex);
            throw;
        }
    }

    private async Task RecordDecisionAsync(
        WorkflowState workflow,
        ApprovalDecision decision,
        ActionExecution? actionExecution,
        SupportCase? supportCase,
        long expectedVersion,
        CancellationToken cancellationToken)
    {
        using var activity = WorkflowTelemetry.StartActivity(
            "persistence.approval.record",
            workflow.Id,
            workflow.TraceId);
        activity?.SetTag("approval.decision", decision.Decision);
        activity?.SetTag("action.type", actionExecution?.ActionType);
        activity?.SetTag("support_case.created", supportCase is not null);
        try
        {
            await _workflowActionRepository.RecordDecisionAsync(
                workflow,
                decision,
                actionExecution,
                supportCase,
                expectedVersion,
                cancellationToken);
            WorkflowTelemetry.RecordSuccess(activity);
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(activity, ex);
            throw;
        }
    }

    private async Task<SupportCase?> LoadSupportCaseAsync(
        Guid workflowId,
        CancellationToken cancellationToken)
    {
        using var activity = WorkflowTelemetry.StartActivity(
            "persistence.support_case.get",
            workflowId);
        try
        {
            var supportCase = await _workflowActionRepository.GetSupportCaseAsync(
                workflowId,
                cancellationToken);
            activity?.SetTag("persistence.found", supportCase is not null);
            WorkflowTelemetry.RecordSuccess(activity);
            return supportCase;
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(activity, ex);
            throw;
        }
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
