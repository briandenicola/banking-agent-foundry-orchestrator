using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Azure.Core;
using Azure.Identity;
using BankingAgent.Application;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace BankingAgent.Infrastructure;

public sealed class CustomerProfileClientOptions
{
    public string? ProjectEndpoint { get; set; }
    public string AgentName { get; set; } = "customer-profile";
    public string? MemoryStoreName { get; set; }
    public string Scope { get; set; } = "https://ai.azure.com/.default";
    public int TimeoutSeconds { get; set; } = 120;
}

/// <summary>
/// Calls the Foundry <c>customer-profile</c> prompt agent over the Responses API.
///
/// Unlike the hosted agents, which run our own LangGraph code in a container and
/// are reached over MCP, this agent is defined entirely by a model, an
/// instruction, and a tool list. Foundry runs the loop. There is therefore no
/// orchestration in this class -- it sends a turn and reports what Foundry did.
///
/// Memory is scoped by <c>{{$userId}}</c>, which Foundry resolves from the
/// caller's Entra token. Because this runs as the orchestrator's managed
/// identity, every caller through the API shares one scope. That is acceptable
/// for a demonstration and wrong for real customers; see backlog item 14.
/// </summary>
public sealed class CustomerProfileClient : ICustomerProfileClient
{
    private const string MemoryApiVersion = "2025-11-15-preview";
    private const string MemoryProbe =
        "What do you remember about me? Answer in one short sentence.";

    private readonly HttpClient _httpClient;
    private readonly ILogger<CustomerProfileClient> _logger;
    private readonly CustomerProfileClientOptions _options;
    private readonly TokenCredential _credential;
    private readonly string? _endpoint;

    public CustomerProfileClient(
        HttpClient httpClient,
        ILogger<CustomerProfileClient> logger,
        IOptions<CustomerProfileClientOptions>? options = null,
        TokenCredential? credential = null)
    {
        _httpClient = httpClient;
        _logger = logger;
        _options = options?.Value ?? new CustomerProfileClientOptions();
        _credential = credential ?? new DefaultAzureCredential(new DefaultAzureCredentialOptions
        {
            ExcludeVisualStudioCodeCredential = true,
            ExcludeInteractiveBrowserCredential = true
        });
        _endpoint = _options.ProjectEndpoint?.TrimEnd('/');
        _httpClient.Timeout = TimeSpan.FromSeconds(_options.TimeoutSeconds);
    }

    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(_endpoint)
        && !string.IsNullOrWhiteSpace(_options.MemoryStoreName);

    public Task<ProfileReply> GetMemoriesAsync(CancellationToken cancellationToken) =>
        AskAsync(MemoryProbe, cancellationToken);

    public Task<ProfileReply> AskAsync(string message, CancellationToken cancellationToken) =>
        AskAsync(message, null, cancellationToken);

    public async Task<ProfileReply> AskAsync(
        string message,
        string? memoryScope,
        CancellationToken cancellationToken)
    {
        EnsureConfigured();
        ArgumentException.ThrowIfNullOrWhiteSpace(message);

        var requestedScope = string.IsNullOrWhiteSpace(memoryScope) ? null : memoryScope.Trim();

        // No previous_response_id: every turn is independent, so anything the
        // agent recalls came out of the memory store and not the prompt.
        object agentReference = requestedScope is null
            ? new { type = "agent_reference", name = _options.AgentName }
            : new { type = "agent_reference", name = _options.AgentName, scope = requestedScope };

        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            $"{_endpoint}/openai/v1/responses")
        {
            Content = JsonContent.Create(new
            {
                agent_reference = agentReference,
                input = message
            })
        };
        await AuthorizeAsync(request, cancellationToken);

        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "customer-profile returned {StatusCode}: {Body}",
                (int)response.StatusCode,
                body);
            throw new CustomerProfileException(
                $"The profile agent returned {(int)response.StatusCode}.");
        }

        var reply = Parse(body);
        if (requestedScope is null)
        {
            return reply;
        }

        var confined = EnforceScope(reply, requestedScope);
        if (confined.Memories.Count < reply.Memories.Count)
        {
            // Foundry accepted the request but answered from a different scope.
            // Treating that as "no memories" is the only safe reading: the
            // alternative is personalising one customer's workflow with
            // another customer's remembered details.
            _logger.LogWarning(
                "customer-profile returned {Returned} memories but only {Kept} were scoped to {Scope}; the rest were discarded. Per-customer memory scoping is not being honoured by the service.",
                reply.Memories.Count,
                confined.Memories.Count,
                requestedScope);
        }

        return confined;
    }

    /// <summary>
    /// Drops memories that came back under a scope other than the one asked
    /// for. Exposed for testing because this is the guard that turns a silently
    /// ignored scope into lost personalisation instead of a data leak.
    /// </summary>
    internal static ProfileReply EnforceScope(ProfileReply reply, string requestedScope)
    {
        var kept = reply.Memories
            .Where(memory => string.Equals(memory.Scope, requestedScope, StringComparison.Ordinal))
            .ToList();

        return reply with { Memories = kept, Scope = requestedScope };
    }

    public async Task ClearMemoriesAsync(CancellationToken cancellationToken)
    {
        EnsureConfigured();

        // Per-item deletion is rejected by the preview API for the identifiers
        // memory search returns, so the store is deleted and recreated from its
        // own definition. This clears every scope, not just the caller's.
        var storeUrl =
            $"{_endpoint}/memory_stores/{_options.MemoryStoreName}?api-version={MemoryApiVersion}";
        var store = await SendMemoryStoreAsync(HttpMethod.Get, storeUrl, null, cancellationToken);

        using var document = JsonDocument.Parse(store);
        var root = document.RootElement;
        if (!root.TryGetProperty("definition", out var definition))
        {
            throw new CustomerProfileException(
                $"Memory store {_options.MemoryStoreName} has no definition to restore.");
        }

        var payload = new
        {
            name = root.TryGetProperty("name", out var name)
                ? name.GetString()
                : _options.MemoryStoreName,
            description = root.TryGetProperty("description", out var description)
                ? description.GetString() ?? string.Empty
                : string.Empty,
            definition = JsonSerializer.Deserialize<JsonElement>(definition.GetRawText())
        };

        await SendMemoryStoreAsync(HttpMethod.Delete, storeUrl, null, cancellationToken);
        await SendMemoryStoreAsync(
            HttpMethod.Post,
            $"{_endpoint}/memory_stores?api-version={MemoryApiVersion}",
            payload,
            cancellationToken);
    }

    private async Task<string> SendMemoryStoreAsync(
        HttpMethod method,
        string url,
        object? payload,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(method, url);
        if (payload is not null)
        {
            request.Content = JsonContent.Create(payload);
        }

        await AuthorizeAsync(request, cancellationToken);
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            _logger.LogError(
                "Memory store call {Method} returned {StatusCode}: {Body}",
                method,
                (int)response.StatusCode,
                body);
            throw new CustomerProfileException(
                $"The memory store returned {(int)response.StatusCode}.");
        }

        return body;
    }

    private async Task AuthorizeAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var token = await _credential.GetTokenAsync(
            new TokenRequestContext([_options.Scope]),
            cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
    }

    private void EnsureConfigured()
    {
        if (!IsConfigured)
        {
            throw new CustomerProfileException(
                "The customer profile agent is not configured.");
        }
    }

    /// <summary>
    /// Reads the reply, the tools Foundry ran, and the memories the memory tool
    /// itself returned. The memories deliberately come from the
    /// <c>memory_search_call</c> item and not from the model's prose, so the UI
    /// shows what the store holds rather than what the model says it holds.
    /// </summary>
    internal static ProfileReply Parse(string body)
    {
        using var document = JsonDocument.Parse(body);
        if (!document.RootElement.TryGetProperty("output", out var output)
            || output.ValueKind != JsonValueKind.Array)
        {
            return new ProfileReply(string.Empty, [], [], null);
        }

        var text = new List<string>();
        var tools = new List<string>();
        var memories = new List<ProfileMemory>();
        string? scope = null;

        foreach (var item in output.EnumerateArray())
        {
            var type = item.TryGetProperty("type", out var typeValue)
                ? typeValue.GetString()
                : null;
            if (type is null)
            {
                continue;
            }

            if (type == "message")
            {
                if (item.TryGetProperty("content", out var content)
                    && content.ValueKind == JsonValueKind.Array)
                {
                    foreach (var part in content.EnumerateArray())
                    {
                        if (part.TryGetProperty("text", out var partText)
                            && partText.ValueKind == JsonValueKind.String)
                        {
                            text.Add(partText.GetString() ?? string.Empty);
                        }
                    }
                }

                continue;
            }

            tools.Add(type);

            if (type != "memory_search_call"
                || !item.TryGetProperty("memories", out var stored)
                || stored.ValueKind != JsonValueKind.Array)
            {
                continue;
            }

            foreach (var memory in stored.EnumerateArray())
            {
                var memoryScope = memory.TryGetProperty("scope", out var scopeValue)
                    ? scopeValue.GetString()
                    : null;
                scope ??= memoryScope;
                memories.Add(new ProfileMemory(
                    memory.TryGetProperty("kind", out var kind)
                        ? kind.GetString() ?? "memory"
                        : "memory",
                    memory.TryGetProperty("content", out var contentValue)
                        ? contentValue.GetString() ?? string.Empty
                        : string.Empty,
                    memoryScope ?? string.Empty));
            }
        }

        return new ProfileReply(
            string.Join("\n\n", text).Trim(),
            tools,
            memories,
            scope);
    }
}

public sealed class CustomerProfileException : Exception
{
    public CustomerProfileException(string message) : base(message)
    {
    }
}
