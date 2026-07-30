namespace BankingAgent.Application;

public interface IMcpClient
{
    Task<McpToolResult> InvokeAsync(
        string toolName,
        IDictionary<string, object?> parameters,
        CancellationToken cancellationToken = default);
}

public sealed record McpToolResult(
    string ToolName,
    string Status,
    string Message,
    IReadOnlyDictionary<string, object?> Data);
