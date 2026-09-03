namespace BankingAgent.Application;

/// <summary>
/// A single memory Foundry has retained, as reported by the memory tool itself
/// rather than by the model's prose. This is the difference between showing the
/// store's own account of what it holds and asking the model to describe it.
/// </summary>
public sealed record ProfileMemory(string Kind, string Content, string Scope);

/// <summary>
/// The outcome of one turn with the customer-profile agent, including which
/// Foundry-managed tools ran. The tool list is the evidence that memory search
/// and the code interpreter executed rather than being simulated by the model.
/// </summary>
public sealed record ProfileReply(
    string Text,
    IReadOnlyList<string> ToolsUsed,
    IReadOnlyList<ProfileMemory> Memories,
    string? Scope);

/// <summary>
/// Talks to the Foundry-hosted <c>customer-profile</c> prompt agent.
/// Foundry runs the model loop, decides when to search and write memory, and
/// executes the code interpreter, so there is deliberately no orchestration here.
/// </summary>
public interface ICustomerProfileClient
{
    /// <summary>Whether the agent and its memory store are configured.</summary>
    bool IsConfigured { get; }

    /// <summary>
    /// Sends one turn. Each call is independent, with no conversation history,
    /// so anything recalled came from the memory store rather than the prompt.
    /// </summary>
    Task<ProfileReply> AskAsync(string message, CancellationToken cancellationToken);

    /// <summary>
    /// Sends one turn against a specific memory scope, so a workflow running in
    /// the background can read the profile of the customer it belongs to rather
    /// than the profile of the identity the orchestrator happens to run as.
    /// </summary>
    /// <param name="memoryScope">
    /// The scope to read and write. Null falls back to the scope Foundry
    /// derives from the caller's own token.
    /// </param>
    /// <remarks>
    /// Implementations must discard any memory the service returns under a
    /// different scope than the one requested. Foundry has been observed to
    /// accept and silently ignore scope-like fields, and a silently ignored
    /// scope would surface one customer's memories inside another customer's
    /// workflow.
    /// </remarks>
    Task<ProfileReply> AskAsync(string message, string? memoryScope, CancellationToken cancellationToken);

    /// <summary>Reads what is currently retained for the caller's scope.</summary>
    Task<ProfileReply> GetMemoriesAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Clears retained memories by recreating the store. Per-item deletion is
    /// rejected by the preview API for the identifiers memory search returns,
    /// so this is deliberately blunt: it clears every scope, not just this one.
    /// </summary>
    Task ClearMemoriesAsync(CancellationToken cancellationToken);
}
