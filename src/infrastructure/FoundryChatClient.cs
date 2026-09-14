using Azure.AI.Extensions.OpenAI;
using Azure.Core;
using Azure.Identity;
using Microsoft.Agents.AI.Foundry;
using Microsoft.Extensions.AI;

namespace BankingAgent.Infrastructure;

public sealed class FoundryChatClientOptions
{
    /// <summary>The Foundry project endpoint. Same value the MCP and memory clients use.</summary>
    public string? ProjectEndpoint { get; set; }

    /// <summary>
    /// The chat model deployment. Convention, matching
    /// <c>infrastructure/locals.tf</c>; overridable for environments that
    /// deploy a different model.
    /// </summary>
    public string ModelDeploymentName { get; set; } = "gpt-5.4-mini";
}

/// <summary>
/// Builds the orchestrator's own <see cref="IChatClient"/> against the Foundry
/// project's Responses API.
///
/// This is the first model access the C# orchestrator has ever had. Until
/// <see href="../../docs/decisions/0007-agent-harness-inner-loop.md">ADR 0007</see>
/// the orchestrator made no model calls at all -- it sequenced hosted agents
/// over MCP and nothing else -- so this type is the whole of that change, and
/// it is deliberately the only place in the codebase that constructs one.
///
/// Authentication
/// --------------
/// No keys, per constitution principle 1. The orchestrator's managed identity
/// already carries <c>Cognitive Services User</c> on the Foundry account
/// (<c>apps/roles.tf</c>), whose data actions are <c>Microsoft.CognitiveServices/*</c>,
/// and <c>CustomerProfileClient</c> already reaches
/// <c>{endpoint}/openai/v1/responses</c> with that identity. This client calls
/// the same API on the same endpoint as the same identity, so it needs no new
/// role assignment -- a point ADR 0007 originally got wrong by asking for
/// <c>Azure AI User</c>.
///
/// Responses, not Chat Completions
/// -------------------------------
/// <see cref="ProjectResponsesClient"/> is Responses-based, which is what
/// <see href="../../docs/decisions/0006-responses-api.md">ADR 0006</see>
/// requires. <c>WithStoredOutputDisabled</c> is the deliberate variant: server-side
/// response storage would put customer conversation content in a Foundry-managed
/// store that is neither the audit trail nor the scoped memory store, creating a
/// third home for customer data that nothing in this system reads.
/// </summary>
public static class FoundryChatClientFactory
{
    public static bool IsConfigured(FoundryChatClientOptions options) =>
        !string.IsNullOrWhiteSpace(options.ProjectEndpoint)
        && !string.IsNullOrWhiteSpace(options.ModelDeploymentName);

    public static IChatClient Create(
        FoundryChatClientOptions options,
        TokenCredential? credential = null)
    {
        ArgumentNullException.ThrowIfNull(options);

        if (!IsConfigured(options))
        {
            throw new InvalidOperationException(
                "Foundry chat client is not configured: FOUNDRY_AGENT_ENDPOINT and a model "
                + "deployment name are both required.");
        }

        // TokenCredential derives from System.ClientModel's AuthenticationTokenProvider,
        // so the Azure.Identity credential is passed straight through with no adapter.
        var tokenProvider = credential ?? new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true
        });

        var responsesClient = new ProjectResponsesClient(
            new Uri(options.ProjectEndpoint!.TrimEnd('/')),
            tokenProvider);

        return responsesClient.AsIChatClientWithStoredOutputDisabled(options.ModelDeploymentName);
    }
}
