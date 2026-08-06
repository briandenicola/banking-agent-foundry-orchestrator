using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using BankingAgent.Domain;
using Microsoft.Agents.AI.Workflows;
using Microsoft.Agents.AI.Workflows.InProc;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Application;

internal sealed class AgentFrameworkWorkflowOrchestrator
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly IMcpClient _mcpClient;
    private readonly ILogger<AgentFrameworkWorkflowOrchestrator> _logger;
    private readonly Func<string, string, Guid, string, IDictionary<string, object?>, DemoScenarioFault, CancellationToken, Task<McpToolResult>> _invokeAgentAsync;

    public AgentFrameworkWorkflowOrchestrator(
        IMcpClient mcpClient,
        ILogger<AgentFrameworkWorkflowOrchestrator> logger,
        Func<string, string, Guid, string, IDictionary<string, object?>, DemoScenarioFault, CancellationToken, Task<McpToolResult>>? invokeAgentAsync = null)
    {
        _mcpClient = mcpClient;
        _logger = logger;
        _invokeAgentAsync = invokeAgentAsync ?? DefaultInvokeAgentAsync;
    }

    public async Task<AgentFrameworkWorkflowExecution> ExecuteAsync(
        WorkflowState workflow,
        DemoScenarioDefinition? demoScenario,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        var initialContext = new WorkflowExecutionContext(
            workflow,
            demoScenario,
            workflow.UserMessage,
            workflow.TraceId,
            workflow.Id);

        try
        {
            var workflowDefinition = BuildWorkflow();
            var environment = CreateExecutionEnvironment();
            var run = await environment.RunAsync(
                workflowDefinition,
                initialContext,
                workflow.Id.ToString("N"),
                cancellationToken);

            var newEvents = run.NewEvents.ToList();
            ThrowIfCanceled(newEvents, cancellationToken);
            var output = await ExtractOutputAsync(newEvents, cancellationToken);
            return output ?? new AgentFrameworkWorkflowExecution(
                initialContext,
                Failed: true,
                ErrorMessage: "The Agent Framework workflow did not emit a terminal output.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Agent Framework workflow execution failed for workflow {WorkflowId}", workflow.Id);
            return new AgentFrameworkWorkflowExecution(
                initialContext,
                Failed: true,
                ErrorMessage: ex.Message);
        }
    }

    private Task<McpToolResult> DefaultInvokeAgentAsync(
        string toolName,
        string agentName,
        Guid workflowId,
        string traceId,
        IDictionary<string, object?> parameters,
        DemoScenarioFault demoFault,
        CancellationToken cancellationToken)
    {
        _ = agentName;
        _ = workflowId;
        _ = traceId;
        _ = demoFault;
        return _mcpClient.InvokeAsync(toolName, parameters, cancellationToken);
    }

    private Workflow BuildWorkflow()
    {
        var plannerBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<WorkflowExecutionContext>>)(
            async (context, workflowContext, cancellationToken) =>
                await ExecutePlannerStepAsync(context, workflowContext, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("planner", null, false);

        var routingBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<WorkflowExecutionContext>>)(
            async (context, workflowContext, cancellationToken) =>
                await ExecuteRoutingStepAsync(context, workflowContext, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("routing", null, false);

        var specialistBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<WorkflowExecutionContext>>)(
            async (context, workflowContext, cancellationToken) =>
                await ExecuteSpecialistStepAsync(context, workflowContext, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("specialist", null, false);

        var terminalBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<string>>)(
            async (context, workflowContext, cancellationToken) =>
                await ExecuteTerminalStepAsync(context, workflowContext, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, string>("terminal", null, false);

        return new WorkflowBuilder(plannerBinding)
            .AddEdge(plannerBinding, routingBinding)
            .AddEdge(routingBinding, specialistBinding)
            .AddEdge(specialistBinding, terminalBinding)
            .WithOutputFrom(terminalBinding)
            .WithName("banking-agent-routing")
            .Build();
    }

    private async Task<WorkflowExecutionContext> ExecutePlannerStepAsync(
        WorkflowExecutionContext context,
        IWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Failed)
        {
            return context;
        }

        var plannerParameters = new Dictionary<string, object?>
        {
            ["user_message"] = context.UserMessage,
            ["trace_id"] = context.TraceId,
            ["workflow_id"] = context.WorkflowId.ToString(),
            ["workflow_status"] = "planning",
            ["correlation_id"] = WorkflowTelemetry.GetCorrelationId()
        };

        _logger.LogDebug(
            "Agent Framework planner step starting for workflow {WorkflowId}.",
            context.WorkflowId);
        try
        {
            var plannerResult = await _invokeAgentAsync(
                "workflow.plan",
                "workflow-planning",
                context.WorkflowId,
                context.TraceId,
                plannerParameters,
                context.DemoScenario?.Fault ?? DemoScenarioFault.None,
                cancellationToken);
            if (!TryReadAgentResult(plannerResult, "workflow-planning", out var plannerDecision, out var error))
            {
                return context with
                {
                    Failed = true,
                    ErrorMessage = error,
                    PlannerResult = plannerResult
                };
            }

            return context with
            {
                PlannerResult = plannerResult,
                PlannerDecision = plannerDecision,
                Intent = plannerDecision.Intent,
                Summary = plannerDecision.Summary,
                SelectedAgent = plannerDecision.SelectedAgent,
                RequiresApproval = plannerDecision.RequiresApproval
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            _logger.LogError(ex, "Planner invocation failed for workflow {WorkflowId}", context.WorkflowId);
            return context with
            {
                Failed = true,
                ErrorMessage = "Planner invocation failed."
            };
        }
    }

    private Task<WorkflowExecutionContext> ExecuteRoutingStepAsync(
        WorkflowExecutionContext context,
        IWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Failed)
        {
            return Task.FromResult(context);
        }

        _logger.LogDebug(
            "Agent Framework routing step starting for workflow {WorkflowId}.",
            context.WorkflowId);
        var policyRoute = WorkflowRoutingPolicy.Decide(context.UserMessage);
        var plannerSelectedAgent = context.PlannerDecision?.SelectedAgent;
        var normalizedPlannerAgent = NormalizeSpecialistAgent(plannerSelectedAgent);
        var routeFallbackReason = ResolveRouteFallbackReason(plannerSelectedAgent, normalizedPlannerAgent);
        var route = routeFallbackReason is null
            ? new WorkflowRoute(
                normalizedPlannerAgent!,
                context.RequiresApproval || policyRoute.RequiresApproval)
            : policyRoute;

        return Task.FromResult(context with
        {
            Route = route,
            PolicyRoute = policyRoute,
            RouteFallbackReason = routeFallbackReason,
            RequiresApproval = route.RequiresApproval
        });
    }

    private async Task<WorkflowExecutionContext> ExecuteSpecialistStepAsync(
        WorkflowExecutionContext context,
        IWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (context.Failed || context.PlannerDecision is null || context.Route is null)
        {
            return context;
        }

        _logger.LogDebug(
            "Agent Framework specialist step starting for workflow {WorkflowId} with agent {AgentName}.",
            context.WorkflowId,
            context.Route.Agent);
        var specialistTool = ResolveSpecialistToolName(context.Route.Agent);
        if (string.IsNullOrWhiteSpace(specialistTool))
        {
            return context with
            {
                Failed = true,
                ErrorMessage = $"Routing policy selected unsupported agent '{context.Route.Agent}'."
            };
        }

        var specialistParameters = new Dictionary<string, object?>(new Dictionary<string, object?>
        {
            ["user_message"] = context.UserMessage,
            ["trace_id"] = context.TraceId,
            ["workflow_id"] = context.WorkflowId.ToString(),
            ["workflow_status"] = "specialist_processing",
            ["intent"] = context.PlannerDecision.Intent,
            ["correlation_id"] = WorkflowTelemetry.GetCorrelationId(),
            ["context"] = new Dictionary<string, object?>
            {
                ["planner_summary"] = context.PlannerDecision.Summary,
                ["planner_evidence"] = context.PlannerDecision.Evidence,
                ["planner_selected_agent"] = context.PlannerDecision.SelectedAgent,
                ["selected_agent"] = context.Route.Agent
            }
        });

        try
        {
            var specialistResult = await _invokeAgentAsync(
                specialistTool,
                context.Route.Agent,
                context.WorkflowId,
                context.TraceId,
                specialistParameters,
                context.DemoScenario?.Fault ?? DemoScenarioFault.None,
                cancellationToken);
            if (!TryReadAgentResult(specialistResult, context.Route.Agent, out var specialistDecision, out var error))
            {
                return context with
                {
                    Failed = true,
                    ErrorMessage = error,
                    SpecialistResult = specialistResult
                };
            }

            return context with
            {
                SpecialistResult = specialistResult,
                SpecialistDecision = specialistDecision,
                Intent = specialistDecision.Intent,
                Summary = specialistDecision.Summary,
                RequiresApproval = specialistDecision.RequiresApproval || context.RequiresApproval
            };
        }
        catch (Exception ex) when (ex is HttpRequestException or TimeoutException)
        {
            _logger.LogError(ex, "Specialist invocation failed for workflow {WorkflowId}", context.WorkflowId);
            return context with
            {
                Failed = true,
                ErrorMessage = "Specialist invocation failed."
            };
        }
    }

    private Task<string> ExecuteTerminalStepAsync(
        WorkflowExecutionContext context,
        IWorkflowContext workflowContext,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        _logger.LogDebug(
            "Agent Framework terminal step starting for workflow {WorkflowId}.",
            context.WorkflowId);

        var finalContext = context.Failed
            ? context
            : context with
            {
                FinalRequiresApproval = context.RequiresApproval,
                FinalIntent = context.Intent,
                FinalSummary = context.Summary
            };

        var serializedFinalContext = JsonSerializer.Serialize(finalContext, JsonOptions);
        return Task.FromResult(serializedFinalContext);
    }

    private static IWorkflowExecutionEnvironment CreateExecutionEnvironment()
    {
        var executionModeType = typeof(InProcessExecutionEnvironment)
            .Assembly
            .GetType("Microsoft.Agents.AI.Workflows.ExecutionMode", throwOnError: true)!;
        var lockstepMode = Enum.Parse(executionModeType, "Lockstep");
        var constructor = typeof(InProcessExecutionEnvironment).GetConstructors(
            BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public)[0];
        return (IWorkflowExecutionEnvironment)constructor.Invoke([lockstepMode, false, null])!;
    }

    private static void ThrowIfCanceled(IReadOnlyList<Microsoft.Agents.AI.Workflows.WorkflowEvent> newEvents, CancellationToken cancellationToken)
    {
        foreach (var workflowEvent in newEvents)
        {
            if (workflowEvent is WorkflowErrorEvent workflowError &&
                workflowError.Data is Exception exception &&
                exception is OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (workflowEvent is ExecutorFailedEvent executorFailed &&
                executorFailed.Data is Exception failedException &&
                failedException is OperationCanceledException)
            {
                throw new OperationCanceledException(cancellationToken);
            }

            if (workflowEvent.Data?.ToString()?.Contains("OperationCanceledException", StringComparison.Ordinal) == true)
            {
                throw new OperationCanceledException(cancellationToken);
            }
        }
    }

    private static Task<AgentFrameworkWorkflowExecution?> ExtractOutputAsync(
        IReadOnlyList<Microsoft.Agents.AI.Workflows.WorkflowEvent> newEvents,
        CancellationToken cancellationToken)
    {
        var output = newEvents
            .OfType<WorkflowOutputEvent>()
            .LastOrDefault();

        if (output?.Data is string serializedWorkflowContext)
        {
            var deserializedContext = JsonSerializer.Deserialize<WorkflowExecutionContext>(serializedWorkflowContext, JsonOptions);
            if (deserializedContext is not null)
            {
                return Task.FromResult<AgentFrameworkWorkflowExecution?>(new AgentFrameworkWorkflowExecution(
                    deserializedContext,
                    deserializedContext.Failed,
                    deserializedContext.ErrorMessage));
            }
        }

        if (output?.Data is WorkflowExecutionContext workflowContextOutput)
        {
            return Task.FromResult<AgentFrameworkWorkflowExecution?>(new AgentFrameworkWorkflowExecution(
                workflowContextOutput,
                workflowContextOutput.Failed,
                workflowContextOutput.ErrorMessage));
        }

        return Task.FromResult<AgentFrameworkWorkflowExecution?>(null);
    }

    private static string? ResolveSpecialistToolName(string agentName) => agentName switch
    {
        "transaction-explanation" => "transaction.explain",
        "suspicious-activity" => "suspicious.assess",
        "dispute-planning" => "dispute.plan",
        _ => null
    };

    private static string? NormalizeSpecialistAgent(string? agentName) =>
        agentName?.Trim().ToLowerInvariant() switch
        {
            "transaction-explanation" => "transaction-explanation",
            "suspicious-activity" => "suspicious-activity",
            "dispute-planning" => "dispute-planning",
            _ => null
        };

    private static string? ResolveRouteFallbackReason(string? plannerSelectedAgent, string? normalizedPlannerAgent)
    {
        if (string.IsNullOrWhiteSpace(plannerSelectedAgent))
        {
            return "missing_selected_agent";
        }

        return normalizedPlannerAgent is null
            ? "unknown_selected_agent"
            : null;
    }

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

}

internal sealed record AgentDecision(
    string Agent,
    string Status,
    string Intent,
    string Summary,
    [property: JsonPropertyName("requires_approval")] bool RequiresApproval,
    [property: JsonPropertyName("selected_agent")] string? SelectedAgent,
    IReadOnlyList<string>? Evidence,
    [property: JsonPropertyName("contract_version")] string? ContractVersion = null,
    [property: JsonPropertyName("execution_mode")] string? ExecutionMode = null);

internal sealed record AgentFrameworkWorkflowExecution(
    WorkflowExecutionContext Context,
    bool Failed,
    string? ErrorMessage);

internal sealed record WorkflowExecutionContext(
    WorkflowState CurrentState,
    DemoScenarioDefinition? DemoScenario,
    string UserMessage,
    string TraceId,
    Guid WorkflowId,
    McpToolResult? PlannerResult = null,
    McpToolResult? SpecialistResult = null,
    AgentDecision? PlannerDecision = null,
    AgentDecision? SpecialistDecision = null,
    WorkflowRoute? Route = null,
    WorkflowRoute? PolicyRoute = null,
    string? RouteFallbackReason = null,
    bool Failed = false,
    string? ErrorMessage = null,
    string? Intent = null,
    string? Summary = null,
    bool RequiresApproval = false,
    bool FinalRequiresApproval = false,
    string? FinalIntent = null,
    string? FinalSummary = null,
    string? SelectedAgent = null);
