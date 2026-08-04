namespace BankingAgent.Application;

public interface IMcpClient
{
    Task<McpToolResult> InvokeAsync(
        string toolName,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(
        string? agentName = null,
        CancellationToken cancellationToken = default)
        => Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);
}

public sealed record McpToolResult(
    string ToolName,
    string Status,
    string Message,
    IReadOnlyDictionary<string, object?> Data);

public sealed record McpToolDefinition(
    string Name,
    string Description,
    string AgentName,
    string Endpoint,
    IReadOnlyDictionary<string, object?> InputSchema,
    string DiscoverySource,
    bool IsDefault = false);
