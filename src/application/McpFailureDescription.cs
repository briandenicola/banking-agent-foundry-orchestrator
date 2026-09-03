using System.Text;
using System.Text.Json;

namespace BankingAgent.Application;

/// <summary>
/// Builds the failure text recorded when a tool call comes back unsuccessful.
///
/// The tool's own <see cref="McpToolResult.Message"/> is a fixed summary such as
/// "MCP tool invocation failed", which says a call failed but not why. The cause
/// -- a JSON-RPC error code, a transport error, the server's own message -- is
/// carried in the result metadata, and was previously dropped on every failure
/// path. That left the log line and the workflow's error text identical for a
/// protocol rejection, a timeout, and an unreachable endpoint, so diagnosing a
/// failed workflow meant reproducing it rather than reading it.
/// </summary>
internal static class McpFailureDescription
{
    /// <summary>
    /// Metadata keys worth appending, in the order they are most useful when
    /// reading a failure. Deliberately an allow-list: the metadata also carries
    /// endpoints and request payloads, and this text is persisted and shown in
    /// the UI.
    /// </summary>
    private static readonly string[] DiagnosticKeys = ["error_code", "error_message", "attempts"];

    public static string Describe(McpToolResult result)
    {
        var message = $"Tool {result.ToolName} failed with status '{result.Status}': {result.Message}";
        var diagnostics = new StringBuilder();

        foreach (var key in DiagnosticKeys)
        {
            if (!result.Data.TryGetValue(key, out var value))
            {
                continue;
            }

            var text = Stringify(value);
            if (string.IsNullOrWhiteSpace(text))
            {
                continue;
            }

            diagnostics.Append(diagnostics.Length == 0 ? " (" : "; ").Append(key).Append(": ").Append(text);
        }

        if (diagnostics.Length > 0)
        {
            diagnostics.Append(')');
        }

        return message + diagnostics;
    }

    private static string? Stringify(object? value) => value switch
    {
        null => null,
        string text => text,
        JsonElement element when element.ValueKind == JsonValueKind.String => element.GetString(),
        JsonElement element => element.ToString(),
        _ => value.ToString()
    };
}
