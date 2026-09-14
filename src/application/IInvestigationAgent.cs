namespace BankingAgent.Application;

/// <summary>
/// Runs the specialist step as a bounded investigation loop instead of a single
/// blind call.
///
/// The distinction that matters for
/// <see href="../../docs/decisions/0007-agent-harness-inner-loop.md">ADR 0007</see>
/// is what this does *not* do. The banking reasoning stays in the Foundry-hosted
/// LangGraph agents, reached over MCP exactly as before. What changes is that
/// those MCP calls can now happen more than once, driven by a model that decides
/// whether the evidence gathered so far is sufficient. The decision itself --
/// intent, summary, and above all <c>requires_approval</c> -- is still authored
/// by the hosted specialist and is never synthesised here.
/// </summary>
public interface IInvestigationAgent
{
    /// <summary>
    /// False when the orchestrator has no model configured, in which case the
    /// caller must fall back to a single direct MCP call.
    /// </summary>
    bool IsConfigured { get; }

    Task<InvestigationResult> InvestigateAsync(
        InvestigationRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record InvestigationRequest(
    string SpecialistTool,
    string AgentName,
    Guid WorkflowId,
    string TraceId,
    IReadOnlyDictionary<string, object?> Parameters);

/// <summary>
/// The outcome of an investigation.
///
/// <paramref name="FinalResult"/> is the last result returned by the hosted
/// specialist, not the model's prose. This is the load-bearing choice: it keeps
/// the approval flag and the customer-facing summary authored by the agent that
/// is allowed to author them, and reduces the harness to deciding how the
/// evidence was gathered rather than what it says.
/// </summary>
public sealed record InvestigationResult(
    McpToolResult FinalResult,
    IReadOnlyList<InvestigationTodo> Todos,
    int ToolCallCount,
    int Iterations);

/// <summary>
/// A unit of work the harness planned for itself. Derived state, never
/// authoritative: these are projected into the audit trail for supervisors to
/// read, and nothing reads workflow truth back out of them.
/// </summary>
public sealed record InvestigationTodo(string Title, string Status);
