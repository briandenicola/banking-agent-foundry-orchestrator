using BankingAgent.Application;

namespace BankingAgent.Infrastructure;

public sealed class StubMcpClient : IMcpClient
{
    public Task<McpToolResult> InvokeAsync(string toolName, IDictionary<string, object?> parameters, CancellationToken cancellationToken = default)
    {
        var result = new McpToolResult(
            ToolName: toolName,
            Status: "ok",
            Message: $"Stubbed MCP invocation for {toolName}",
            Data: new Dictionary<string, object?>
            {
                ["tool_name"] = toolName,
                ["parameters"] = parameters
            });

        return Task.FromResult(result);
    }
}
