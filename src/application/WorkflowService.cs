using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace BankingAgent.Application;

public interface IWorkflowService
{
    Task<WorkflowState> StartAsync(string userMessage, CancellationToken cancellationToken = default);
    Task<WorkflowState> StartDemoAsync(string scenarioId, CancellationToken cancellationToken = default);

    /// <summary>
    /// Starts a workflow on behalf of an identified customer. Separate from
    /// <see cref="StartAsync(string, CancellationToken)"/> so existing callers
    /// that have no identity keep compiling and keep the null-customer
    /// behaviour, rather than silently passing an empty identity.
    /// </summary>
    Task<WorkflowState> StartForCustomerAsync(
        string userMessage,
        string? customerId,
        CancellationToken cancellationToken = default);

    Task<WorkflowState> StartDemoForCustomerAsync(
        string scenarioId,
        string? customerId,
        CancellationToken cancellationToken = default);
    Task<WorkflowState> RecoverAsync(Guid workflowId, CancellationToken cancellationToken = default);
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
    private readonly AgentFrameworkWorkflowOrchestrator _agentFrameworkWorkflowOrchestrator;

    public WorkflowService(
        IMcpClient mcpClient,
        ILogger<WorkflowService> logger,
        IWorkflowRepository workflowRepository,
        IWorkflowActionRepository workflowActionRepository,
        IDemoScenarioPolicy? demoScenarioPolicy = null,
        ILoggerFactory? loggerFactory = null,
        ICustomerProfileClient? customerProfile = null)
    {
        _mcpClient = mcpClient;
        _logger = logger;
        _workflowRepository = workflowRepository;
        _workflowActionRepository = workflowActionRepository;
        _demoScenarioPolicy = demoScenarioPolicy ?? DemoScenarioPolicy.Disabled;
        _agentFrameworkWorkflowOrchestrator = new AgentFrameworkWorkflowOrchestrator(
            mcpClient,
            loggerFactory?.CreateLogger<AgentFrameworkWorkflowOrchestrator>()
                ?? NullLogger<AgentFrameworkWorkflowOrchestrator>.Instance,
            (toolName, agentName, workflowId, traceId, parameters, demoFault, cancellationToken) =>
                InvokeAgentAsync(toolName, agentName, workflowId, traceId, parameters, demoFault, cancellationToken),
            customerProfile);
    }

    public Task<WorkflowState> StartAsync(
        string userMessage,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(userMessage, null, null, cancellationToken);

    public Task<WorkflowState> StartDemoAsync(
        string scenarioId,
        CancellationToken cancellationToken = default)
    {
        var scenario = _demoScenarioPolicy.Resolve(scenarioId);
        return StartCoreAsync(scenario.UserMessage, scenario, null, cancellationToken);
    }

    public Task<WorkflowState> StartForCustomerAsync(
        string userMessage,
        string? customerId,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(userMessage, null, customerId, cancellationToken);

    public Task<WorkflowState> StartDemoForCustomerAsync(
        string scenarioId,
        string? customerId,
        CancellationToken cancellationToken = default)
    {
        var scenario = _demoScenarioPolicy.Resolve(scenarioId);
        return StartCoreAsync(scenario.UserMessage, scenario, customerId, cancellationToken);
    }

    private async Task<WorkflowState> StartCoreAsync(
        string userMessage,
        DemoScenarioDefinition? demoScenario,
        string? customerId,
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
                demoScenario.Id));
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
            Version: 0,
            CustomerId: string.IsNullOrWhiteSpace(customerId) ? null : customerId.Trim());

        await AddWorkflowAsync(draftState, cancellationToken);
        return draftState;
    }

    // EnqueueAsync / EnqueueDemoAsync — kept as aliases for backward compat
    // in callers that pre-date the interface rename; delegate to StartAsync.
    // EnqueueAsync / EnqueueDemoAsync — fast-return variant (alias for Start*)
    // endpoint. Persists Draft and returns immediately; execution is deferred to
    // WorkflowRecoveryWorker or the IWorkflowExecutionTrigger best-effort path.
    public Task<WorkflowState> EnqueueAsync(
        string userMessage,
        CancellationToken cancellationToken = default) =>
        StartAsync(userMessage, cancellationToken);

    public Task<WorkflowState> EnqueueDemoAsync(
        string scenarioId,
        CancellationToken cancellationToken = default) =>
        StartDemoAsync(scenarioId, cancellationToken);

    public async Task<WorkflowState> RecoverAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var current = await GetWorkflowAsync(workflowId, cancellationToken)
            ?? throw new WorkflowNotFoundException(workflowId);
        // Accept Draft (not yet claimed) or Recovering (claimed by worker).
        // Draft is valid here because the immediate trigger may call RecoverAsync
        // before the periodic worker has updated the status via ClaimNextAsync.
        // The atomic ClaimNextAsync in IWorkflowRecoveryRepository remains the
        // sole distributed-safe execution authority.
        // Terminal/approval states: return current state (idempotent — another replica
        // may have already completed execution before this call arrived).
        if (current.Status is WorkflowStatus.Completed
            or WorkflowStatus.Failed
            or WorkflowStatus.Rejected
            or WorkflowStatus.WaitingForApproval)
        {
            return current;
        }

        if (current.Status is not (WorkflowStatus.Draft or WorkflowStatus.Recovering))
        {
            throw new InvalidTransitionException(
                workflowId,
                current.Status,
                "Draft or Recovering");
        }

        var scenarioId = current.Events
            .LastOrDefault(workflowEvent => workflowEvent.Type == "workflow.demo_scenario")
            ?.Details;
        DemoScenarioDefinition? demoScenario = null;
        if (!string.IsNullOrWhiteSpace(scenarioId))
        {
            DemoScenarioCatalog.TryGet(scenarioId, out demoScenario);
        }

        using var recoveryActivity = WorkflowTelemetry.StartActivity(
            "workflow.recovery",
            current.Id,
            current.TraceId);
        recoveryActivity?.SetTag("workflow.operation", "recover");
        recoveryActivity?.SetTag("demo.scenario", demoScenario?.Id);
        recoveryActivity?.SetTag("workflow.recovery.attempt_count", current.RecoveryAttemptCount);
        if (current.NextAttemptAt is not null)
        {
            recoveryActivity?.SetTag("workflow.recovery.next_attempt_at", current.NextAttemptAt.Value.ToString("O"));
        }

        try
        {
            var recovered = await ExecuteRoutingAsync(
                current,
                demoScenario,
                cancellationToken);
            WorkflowTelemetry.RecordSuccess(recoveryActivity, recovered.Status.ToString());
            return recovered;
        }
        catch (Exception ex)
        {
            WorkflowTelemetry.RecordFailure(recoveryActivity, ex);
            throw;
        }
    }

    private async Task<WorkflowState> ExecuteRoutingAsync(
        WorkflowState initialState,
        DemoScenarioDefinition? demoScenario,
        CancellationToken cancellationToken)
    {
        var workflowId = initialState.Id;
        var traceId = initialState.TraceId;
        var startedAt = Stopwatch.GetTimestamp();
        AgentFrameworkWorkflowExecution execution;
        try
        {
            execution = await _agentFrameworkWorkflowOrchestrator.ExecuteAsync(
                initialState,
                demoScenario,
                cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            await PersistFailedAsync(
                initialState,
                [],
                "Planner invocation was canceled.",
                CancellationToken.None);
            throw;
        }

        if (execution.Failed)
        {
            return await PersistFailedAsync(
                initialState,
                [],
                execution.ErrorMessage ?? "Workflow execution failed.",
                cancellationToken);
        }

        var current = initialState;
        var plannerResult = execution.Context.PlannerResult;
        if (plannerResult is null)
        {
            return await PersistFailedAsync(
                current,
                [],
                "Planner result was not emitted by the Agent Framework workflow.",
                cancellationToken);
        }

        var profileEvents = CreateProfileRecallEvents(execution.Context);
        var plannerEvent = CreateInvocationEvent(plannerResult, "workflow.plan");
        if (!TryReadAgentResult(plannerResult, "workflow-planning", out var plannerDecision, out var plannerError))
        {
            return await PersistFailedAsync(current, [.. profileEvents, plannerEvent], plannerError, cancellationToken);
        }

        current = await AdvanceAndPersistAsync(current, [.. profileEvents, plannerEvent], cancellationToken);

        var policyRoute = execution.Context.PolicyRoute ?? WorkflowRoutingPolicy.Decide(current.UserMessage);
        var route = execution.Context.Route ?? policyRoute;
        if (!SpecialistTools.TryGetValue(route.Agent, out _))
        {
            return await PersistFailedAsync(
                current,
                [],
                $"Routing policy selected unsupported agent '{route.Agent}'.",
                cancellationToken);
        }

        var routeAuditEvents = CreateRouteAuditEvents(
            plannerDecision,
            policyRoute,
            route,
            execution.Context.RouteFallbackReason);
        var routeEvent = new WorkflowEvent(
            "workflow.route_selected",
            $"Selected specialist {route.Agent}",
            DateTimeOffset.UtcNow,
            "system",
            $"Approval required: {route.RequiresApproval}");

        current = await AdvanceAndPersistAsync(current, [.. routeAuditEvents, routeEvent], cancellationToken);

        var specialistResult = execution.Context.SpecialistResult;
        if (specialistResult is null)
        {
            return await PersistFailedAsync(
                current,
                [],
                "Specialist result was not emitted by the Agent Framework workflow.",
                cancellationToken);
        }

        var specialistEvent = CreateInvocationEvent(specialistResult);
        if (!TryReadAgentResult(specialistResult, route.Agent, out var specialistDecision, out var specialistError))
        {
            return await PersistFailedAsync(current, [specialistEvent], specialistError, cancellationToken);
        }

        var requiresApproval = execution.Context.FinalRequiresApproval || route.RequiresApproval;
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
            Intent = execution.Context.FinalIntent ?? specialistDecision.Intent,
            RequiresApproval = requiresApproval,
            UpdatedAt = now,
            Events = finalEvents,
            Version = current.Version + 1
        };

        await UpdateWorkflowAsync(finalState, current.Version, cancellationToken);
        WorkflowTelemetry.RecordSuccess(Activity.Current, finalState.Status.ToString());

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
            "Workflow {WorkflowId} failed for trace {TraceId} with error code {ErrorCode} after {DurationMs} ms: {Error}",
            current.Id,
            current.TraceId,
            "workflow_execution_failed",
            (now - current.CreatedAt).TotalMilliseconds,
            error);

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
            IReadOnlyList<McpToolDefinition> discoveredTools = [];
            if (demoFault is not (DemoScenarioFault.HostedAgentFailure or DemoScenarioFault.HostedAgentTimeout))
            {
                discoveredTools = await _mcpClient.DiscoverToolsAsync(agentName, cancellationToken);
                activity?.SetTag("agent.discovered_tools.count", discoveredTools.Count);
                if (discoveredTools.Count > 0)
                {
                    activity?.SetTag("agent.discovered_tools", string.Join(",", discoveredTools.Select(tool => tool.Name)));
                }
            }

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
            var (contractVersion, executionMode) = ReadAgentContractMetadata(result);
            activity?.SetTag("agent.contract_version", contractVersion);
            activity?.SetTag("agent.execution_mode", executionMode);
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

    private static WorkflowEvent CreateInvocationEvent(
        McpToolResult result,
        string eventType = "mcp.invoked") =>
        new(
            eventType,
            $"Invoked tool {result.ToolName}",
            DateTimeOffset.UtcNow,
            "system",
            CreateInvocationEventDetails(result));

    /// <summary>
    /// Surfaces what the profile agent recalled onto the workflow timeline.
    ///
    /// Recall already influences the run -- the preferences are handed to the
    /// planner and the specialist -- but that happens inside the model prompt,
    /// where it leaves no trace anyone can inspect. Without this event the only
    /// evidence that memory was consulted is a log line carrying a count, so
    /// the audit trail would show a personalised answer arriving with no record
    /// of what personalised it.
    /// </summary>
    private static WorkflowEvent[] CreateProfileRecallEvents(WorkflowExecutionContext context)
    {
        var preferences = context.RememberedPreferences;
        if (preferences.Count == 0)
        {
            // Covers every fail-open path in the profile step: no signed-in
            // customer, no profile agent, an empty store, or a failed lookup.
            // None of those are events worth recording against the workflow.
            return [];
        }

        return
        [
            new WorkflowEvent(
                "workflow.profile_recalled",
                $"Recalled {preferences.Count} remembered {(preferences.Count == 1 ? "preference" : "preferences")}",
                context.ProfileRecalledAt ?? DateTimeOffset.UtcNow,
                "customer-profile",
                string.Join(" · ", preferences))
        ];
    }

    private static WorkflowEvent[] CreateRouteAuditEvents(
        AgentDecision plannerDecision,
        WorkflowRoute policyRoute,
        WorkflowRoute winningRoute,
        string? fallbackReason)
    {
        if (fallbackReason is not null)
        {
            return
            [
                new WorkflowEvent(
                    "workflow.route_fallback",
                    CreateRouteFallbackMessage(fallbackReason),
                    DateTimeOffset.UtcNow,
                    "system",
                    JsonSerializer.Serialize(new
                    {
                        reason_code = fallbackReason,
                        planner_agent = plannerDecision.SelectedAgent,
                        planner_requires_approval = plannerDecision.RequiresApproval,
                        policy_agent = policyRoute.Agent,
                        policy_requires_approval = policyRoute.RequiresApproval,
                        winning_agent = winningRoute.Agent,
                        winning_requires_approval = winningRoute.RequiresApproval,
                        winner = "policy",
                        agent_winner = "policy",
                        approval_winner = "policy"
                    }))
            ];
        }

        if (string.Equals(plannerDecision.SelectedAgent, policyRoute.Agent, StringComparison.OrdinalIgnoreCase) &&
            plannerDecision.RequiresApproval == policyRoute.RequiresApproval)
        {
            return [];
        }

        return
        [
            new WorkflowEvent(
                "workflow.route_disagreement",
                "Planner route and routing policy disagreed; planner agent selection won and policy approval guardrail was applied.",
                DateTimeOffset.UtcNow,
                "system",
                JsonSerializer.Serialize(new
                {
                    planner_agent = plannerDecision.SelectedAgent,
                    planner_requires_approval = plannerDecision.RequiresApproval,
                    policy_agent = policyRoute.Agent,
                    policy_requires_approval = policyRoute.RequiresApproval,
                    winning_agent = winningRoute.Agent,
                    winning_requires_approval = winningRoute.RequiresApproval,
                    winner = string.Equals(winningRoute.Agent, plannerDecision.SelectedAgent, StringComparison.OrdinalIgnoreCase)
                        ? "planner"
                        : "policy",
                    agent_winner = string.Equals(winningRoute.Agent, plannerDecision.SelectedAgent, StringComparison.OrdinalIgnoreCase)
                        ? "planner"
                        : "policy",
                    approval_winner = winningRoute.RequiresApproval == plannerDecision.RequiresApproval
                        ? "planner"
                        : "policy"
                }))
        ];
    }

    private static string CreateRouteFallbackMessage(string fallbackReason) => fallbackReason switch
    {
        "missing_selected_agent" => "Planner returned no selected_agent; keyword policy selected the specialist.",
        "unknown_selected_agent" => "Planner returned an unrecognized selected_agent; keyword policy selected the specialist.",
        _ => "Planner route was unusable; keyword policy selected the specialist."
    };

    private static string CreateInvocationEventDetails(McpToolResult result)
    {
        var (contractVersion, executionMode) = ReadAgentContractMetadata(result);
        return JsonSerializer.Serialize(new
        {
            status = result.Status,
            message = result.Message,
            contract_version = contractVersion,
            execution_mode = executionMode
        });
    }

    private static (string? ContractVersion, string? ExecutionMode) ReadAgentContractMetadata(
        McpToolResult result)
    {
        if (!result.Data.TryGetValue("response_body", out var responseBodyValue))
        {
            return (null, null);
        }

        var responseBody = responseBodyValue switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            JsonElement jsonElement => jsonElement.ToString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(responseBody))
        {
            return (null, null);
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                return (null, null);
            }

            var contractVersion = ReadOptionalString(root, "contract_version");
            var executionMode = ReadOptionalString(root, "execution_mode");
            return (contractVersion, executionMode);
        }
        catch (JsonException)
        {
            return (null, null);
        }
    }

    private static string? ReadOptionalString(JsonElement root, string propertyName) =>
        root.TryGetProperty(propertyName, out var value) &&
        value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;

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

        if (!result.Data.TryGetValue("response_body", out var responseBodyValue))
        {
            error = $"Tool {result.ToolName} did not return an agent response body.";
            return false;
        }

        var responseBody = responseBodyValue switch
        {
            string text when !string.IsNullOrWhiteSpace(text) => text,
            JsonElement jsonElement when jsonElement.ValueKind == JsonValueKind.String => jsonElement.GetString(),
            JsonElement jsonElement => jsonElement.ToString(),
            _ => null
        };

        if (string.IsNullOrWhiteSpace(responseBody))
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

            if (parsed.ContractVersion is not null &&
                !string.Equals(parsed.ContractVersion, "1.0", StringComparison.Ordinal))
            {
                error = $"Tool {result.ToolName} returned unsupported contract version '{parsed.ContractVersion}'.";
                return false;
            }

            if (parsed.ExecutionMode is not null &&
                parsed.ExecutionMode is not ("model" or "fallback"))
            {
                error = $"Tool {result.ToolName} returned unsupported execution mode '{parsed.ExecutionMode}'.";
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
        IReadOnlyList<string>? Evidence,
        [property: JsonPropertyName("contract_version")] string? ContractVersion = null,
        [property: JsonPropertyName("execution_mode")] string? ExecutionMode = null);
}
