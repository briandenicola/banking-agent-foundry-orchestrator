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
            workflow.Id,
            CancellationToken: cancellationToken);

        try
        {
            var currentContext = initialContext;
            currentContext = await ExecutePlannerStepAsync(currentContext, cancellationToken);
            if (currentContext.Failed)
            {
                return new AgentFrameworkWorkflowExecution(
                    currentContext,
                    Failed: true,
                    ErrorMessage: currentContext.ErrorMessage);
            }

            currentContext = await ExecuteRoutingStepAsync(currentContext, cancellationToken);
            if (currentContext.Failed)
            {
                return new AgentFrameworkWorkflowExecution(
                    currentContext,
                    Failed: true,
                    ErrorMessage: currentContext.ErrorMessage);
            }

            currentContext = await ExecuteSpecialistStepAsync(currentContext, cancellationToken);
            if (currentContext.Failed)
            {
                return new AgentFrameworkWorkflowExecution(
                    currentContext,
                    Failed: true,
                    ErrorMessage: currentContext.ErrorMessage);
            }

            currentContext = await ExecuteTerminalStepAsync(currentContext, cancellationToken);
            return new AgentFrameworkWorkflowExecution(
                currentContext,
                Failed: currentContext.Failed,
                ErrorMessage: currentContext.ErrorMessage);
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
            async (context, _, cancellationToken) =>
                await ExecutePlannerStepAsync(context, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("planner", null, false);

        var routingBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<WorkflowExecutionContext>>)(
            async (context, _, cancellationToken) =>
                await ExecuteRoutingStepAsync(context, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("routing", null, false);

        var specialistBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<WorkflowExecutionContext>>)(
            async (context, _, cancellationToken) =>
                await ExecuteSpecialistStepAsync(context, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("specialist", null, false);

        var terminalBinding = ((Func<WorkflowExecutionContext, IWorkflowContext, CancellationToken, ValueTask<WorkflowExecutionContext>>)(
            async (context, _, cancellationToken) =>
                await ExecuteTerminalStepAsync(context, cancellationToken)))
            .BindAsExecutor<WorkflowExecutionContext, WorkflowExecutionContext>("terminal", null, false);

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
        CancellationToken cancellationToken)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

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

        try
        {
            var plannerResult = await _invokeAgentAsync(
                "workflow.plan",
                "workflow-planning",
                context.WorkflowId,
                context.TraceId,
                plannerParameters,
                context.DemoScenario?.Fault ?? DemoScenarioFault.None,
                context.CancellationToken);
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
        CancellationToken cancellationToken)
    {
        if (context.Failed)
        {
            return Task.FromResult(context);
        }

        var route = WorkflowRoutingPolicy.Decide(context.UserMessage);
        return Task.FromResult(context with
        {
            Route = route,
            RequiresApproval = route.RequiresApproval
        });
    }

    private async Task<WorkflowExecutionContext> ExecuteSpecialistStepAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        context.CancellationToken.ThrowIfCancellationRequested();

        if (context.Failed || context.PlannerDecision is null || context.Route is null)
        {
            return context;
        }

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
                context.CancellationToken);
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

    private Task<WorkflowExecutionContext> ExecuteTerminalStepAsync(
        WorkflowExecutionContext context,
        CancellationToken cancellationToken)
    {
        if (context.Failed)
        {
            return Task.FromResult(context);
        }

        return Task.FromResult(context with
        {
            FinalRequiresApproval = context.RequiresApproval,
            FinalIntent = context.Intent,
            FinalSummary = context.Summary
        });
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

    private static void ThrowIfCanceled(Run run, CancellationToken cancellationToken)
    {
        foreach (var workflowEvent in run.NewEvents)
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

    private static async Task<AgentFrameworkWorkflowExecution?> ExtractOutputAsync(
        Run run,
        CancellationToken cancellationToken)
    {
        await run.GetStatusAsync(cancellationToken);

        var output = run.NewEvents
            .OfType<WorkflowOutputEvent>()
            .LastOrDefault();

        if (output?.Data is not WorkflowExecutionContext workflowContext)
        {
            return null;
        }

        return new AgentFrameworkWorkflowExecution(
            workflowContext,
            workflowContext.Failed,
            workflowContext.ErrorMessage);
    }

    private static string? ResolveSpecialistToolName(string agentName) => agentName switch
    {
        "transaction-explanation" => "transaction.explain",
        "suspicious-activity" => "suspicious.assess",
        "dispute-planning" => "dispute.plan",
        _ => null
    };

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
    CancellationToken CancellationToken = default,
    McpToolResult? PlannerResult = null,
    McpToolResult? SpecialistResult = null,
    AgentDecision? PlannerDecision = null,
    AgentDecision? SpecialistDecision = null,
    WorkflowRoute? Route = null,
    bool Failed = false,
    string? ErrorMessage = null,
    string? Intent = null,
    string? Summary = null,
    bool RequiresApproval = false,
    bool FinalRequiresApproval = false,
    string? FinalIntent = null,
    string? FinalSummary = null,
    string? SelectedAgent = null);
