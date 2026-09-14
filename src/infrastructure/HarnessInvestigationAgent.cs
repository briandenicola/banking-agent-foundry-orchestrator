using System.Text.Json;
using BankingAgent.Application;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Infrastructure;

public sealed class HarnessInvestigationAgentOptions
{
    /// <summary>
    /// Hard ceiling on model turns. A banking investigation that cannot
    /// conclude in a few rounds is a failure, not a reason to keep spending.
    /// </summary>
    public int MaxIterations { get; set; } = 4;

    /// <summary>Hard ceiling on calls into the hosted specialist.</summary>
    public int MaxSpecialistCalls { get; set; } = 3;
}

/// <summary>
/// The specialist step, run as an Agent Framework harness loop (ADR 0007).
///
/// What the harness is allowed to decide
/// -------------------------------------
/// Only *how the evidence was gathered*: whether one call to the hosted
/// specialist was enough, and what to ask it next if it was not. It is given no
/// banking instructions and no authority to answer on its own. The returned
/// decision is the hosted agent's last result verbatim, so
/// <c>requires_approval</c>, the intent, and the customer-facing summary are
/// still authored by the LangGraph agent that ADR 0007 insists must author
/// them. Had the harness been allowed to compose that answer itself, it would
/// be embedding specialist logic in C# and the ADR would be rejecting itself.
///
/// What the model cannot touch
/// ---------------------------
/// The tool exposed to the model takes exactly one argument: a natural-language
/// focus for the next round. Every identifier -- <c>workflow_id</c>,
/// <c>trace_id</c>, <c>correlation_id</c>, the authenticated user message, and
/// the remembered-preference context -- is bound from the request the workflow
/// built, never from model output. A model that hallucinates a different
/// workflow id therefore cannot reach another workflow's data, and the audit
/// trail cannot be broken by a bad generation.
/// </summary>
public sealed class HarnessInvestigationAgent : IInvestigationAgent
{
    internal const string SpecialistFunctionName = "consult_specialist";

    private const string Instructions = """
        You coordinate a banking investigation. You do not perform it.

        The `consult_specialist` tool reaches the specialist system that holds all
        banking authority. It is the only source of findings you may use.

        Rules:
        - Always call `consult_specialist` at least once before finishing.
        - Read its result. If it is complete, stop and reply with a single short
          sentence confirming the investigation is complete.
        - If, and only if, the result is incomplete or ambiguous, call
          `consult_specialist` again with a `focus` naming the specific gap.
        - Never answer a banking question from your own knowledge.
        - Never state, infer, override, or restate whether the request requires
          approval. That is decided elsewhere and is not yours to report.
        """;

    private readonly IChatClient _chatClient;
    private readonly IMcpClient _mcpClient;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<HarnessInvestigationAgent> _logger;
    private readonly HarnessInvestigationAgentOptions _options;

    public HarnessInvestigationAgent(
        IChatClient? chatClient,
        IMcpClient mcpClient,
        ILoggerFactory loggerFactory,
        HarnessInvestigationAgentOptions? options = null)
    {
        _chatClient = chatClient!;
        _mcpClient = mcpClient;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<HarnessInvestigationAgent>();
        _options = options ?? new HarnessInvestigationAgentOptions();
    }

    public bool IsConfigured => _chatClient is not null;

    public async Task<InvestigationResult> InvestigateAsync(
        InvestigationRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (!IsConfigured)
        {
            throw new InvalidOperationException(
                "The investigation agent has no chat client; the caller should have fallen back "
                + "to a direct specialist call.");
        }

        McpToolResult? lastResult = null;
        var toolCalls = 0;

        // Bound independently of the model. MaximumIterationsPerRequest limits
        // model turns; this limits spend against the hosted specialist even if
        // a single turn emits a burst of parallel tool calls.
        async Task<string> ConsultAsync(string? focus = null)
        {
            if (toolCalls >= _options.MaxSpecialistCalls)
            {
                return "The specialist call budget for this investigation is exhausted. "
                    + "Finish with the findings you already have.";
            }

            toolCalls++;

            var parameters = new Dictionary<string, object?>(request.Parameters);
            if (!string.IsNullOrWhiteSpace(focus))
            {
                parameters["investigation_focus"] = focus.Trim();
            }

            var result = await _mcpClient
                .InvokeAsync(request.SpecialistTool, parameters, cancellationToken)
                .ConfigureAwait(false);

            lastResult = result;

            return JsonSerializer.Serialize(new
            {
                status = result.Status,
                message = result.Message,
                data = result.Data
            });
        }

        var specialistTool = AIFunctionFactory.Create(
            ConsultAsync,
            SpecialistFunctionName,
            "Consult the banking specialist system that holds all banking authority. "
                + "Optionally pass `focus` to name a specific gap to investigate next.");

        // The todo provider is constructed here rather than left to the harness
        // default, because a provider we own is one we can read back out of the
        // session afterwards. Supervisor-visible todos are the point of ADR 0007.
        using var todoProvider = new TodoProvider(new TodoProviderOptions());

        var agent = _chatClient.AsHarnessAgent(
            BuildOptions(request, specialistTool, todoProvider),
            _loggerFactory);

        var session = await agent.CreateSessionAsync(cancellationToken).ConfigureAwait(false);
        var response = await agent
            .RunAsync(BuildPrompt(request), session, cancellationToken: cancellationToken)
            .ConfigureAwait(false);

        if (lastResult is null)
        {
            // The loop finished without ever consulting the specialist, so there
            // is no authored decision. Inventing one from the model's prose is
            // exactly what this design exists to prevent.
            throw new InvalidOperationException(
                $"The investigation for workflow {request.WorkflowId} completed without calling "
                + $"'{request.SpecialistTool}'; no specialist decision was produced.");
        }

        var todos = await ReadTodosAsync(todoProvider, session, cancellationToken).ConfigureAwait(false);

        _logger.LogInformation(
            "Investigation for workflow {WorkflowId} consulted {AgentName} {ToolCalls} time(s) "
            + "across {Iterations} model turn(s) and planned {TodoCount} task(s).",
            request.WorkflowId,
            request.AgentName,
            toolCalls,
            response.Messages.Count,
            todos.Count);

        return new InvestigationResult(lastResult, todos, toolCalls, response.Messages.Count);
    }

    private static async Task<IReadOnlyList<InvestigationTodo>> ReadTodosAsync(
        TodoProvider provider,
        AgentSession session,
        CancellationToken cancellationToken)
    {
        var todos = await provider.GetAllTodosAsync(session, cancellationToken).ConfigureAwait(false);

        return todos
            .Select(todo => new InvestigationTodo(
                todo.Title,
                todo.IsComplete ? "completed" : "pending"))
            .ToArray();
    }

    private HarnessAgentOptions BuildOptions(
        InvestigationRequest request,
        AIFunction specialistTool,
        TodoProvider todoProvider) => new()
        {
            Name = $"investigation-{request.AgentName}",
            Description = "Bounded evidence-gathering loop around a hosted banking specialist.",
            HarnessInstructions = Instructions,
            MaximumIterationsPerRequest = _options.MaxIterations,
            ChatOptions = new ChatOptions { Tools = [specialistTool] },

            // --- Capabilities disabled, per ADR 0007 -------------------------
            // A banking answer grounded in an open web search is a compliance
            // incident. Evidence comes from the customer's own records or not at all.
            DisableWebSearch = true,

            // Customer memory already has a scoped home in the Foundry memory store
            // (ADR 0003). File memory would create a second, unscoped surface
            // writing customer notes to container-local disk.
            DisableFileMemory = true,

            // Filesystem-discovered capability injection is an unreviewed code path
            // into an approval-gated workflow.
            DisableAgentSkillsProvider = true,

            // --- Capabilities enabled ----------------------------------------
            // Todos are the supervisor-facing win; agent modes match how this system
            // already thinks; compaction stops a long evidence loop silently
            // truncating evidence; OpenTelemetry is constitution principle 4.
            // Supplied explicitly rather than by default, so the todos can be read
            // back out of the session and projected into the audit trail.
            DisableTodoProvider = true,
            AIContextProviders = [todoProvider],
            DisableAgentModeProvider = false,
            DisableCompaction = false,
            DisableOpenTelemetry = false,
            OpenTelemetrySourceName = WorkflowTelemetrySourceName
        };

    internal const string WorkflowTelemetrySourceName = "BankingAgent.Workflow";

    private static string BuildPrompt(InvestigationRequest request)
    {
        var userMessage = request.Parameters.TryGetValue("user_message", out var message)
            ? message?.ToString()
            : null;
        var intent = request.Parameters.TryGetValue("intent", out var value)
            ? value?.ToString()
            : null;

        return $"""
            Investigate this banking request using the specialist system.

            Intent: {intent ?? "unknown"}
            Customer request: {userMessage ?? "(not supplied)"}
            """;
    }
}
