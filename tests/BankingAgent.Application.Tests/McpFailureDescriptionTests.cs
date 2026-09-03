using BankingAgent.Application;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// A failed tool call previously recorded only its fixed summary, so a protocol
/// rejection, a timeout, and an unreachable endpoint all produced the identical
/// sentence. These tests pin that the cause carried in the result metadata
/// reaches the recorded error, because that text is the only account of the
/// failure that survives in the workflow and in the logs.
/// </summary>
public sealed class McpFailureDescriptionTests
{
    [Fact]
    public void The_underlying_error_message_is_included()
    {
        var described = McpFailureDescription.Describe(Failure(new()
        {
            ["error_message"] = "Agent run ended with status failed",
            ["error_code"] = "-32000"
        }));

        Assert.Contains("Agent run ended with status failed", described);
        Assert.Contains("-32000", described);
    }

    [Fact]
    public void The_tool_and_its_summary_are_still_reported()
    {
        var described = McpFailureDescription.Describe(Failure(new()
        {
            ["error_message"] = "boom"
        }));

        Assert.Contains("transaction.explain", described);
        Assert.Contains("MCP tool invocation failed.", described);
    }

    [Fact]
    public void A_failure_carrying_no_diagnostics_reads_exactly_as_before()
    {
        var described = McpFailureDescription.Describe(Failure([]));

        Assert.Equal(
            "Tool transaction.explain failed with status 'error': MCP tool invocation failed.",
            described);
    }

    [Fact]
    public void Endpoints_and_payloads_are_not_copied_into_the_recorded_error()
    {
        // The metadata also carries the endpoint and request details. This text
        // is persisted and rendered in the UI, so only the allow-listed
        // diagnostic keys belong in it.
        var described = McpFailureDescription.Describe(Failure(new()
        {
            ["error_message"] = "boom",
            ["endpoint"] = "https://internal.example/mcp",
            ["request_body"] = "{\"account\":\"123456\"}"
        }));

        Assert.DoesNotContain("internal.example", described);
        Assert.DoesNotContain("123456", described);
    }

    [Fact]
    public void Blank_diagnostics_do_not_produce_empty_parentheses()
    {
        var described = McpFailureDescription.Describe(Failure(new()
        {
            ["error_message"] = "   "
        }));

        Assert.DoesNotContain("(", described);
    }

    private static McpToolResult Failure(Dictionary<string, object?> data) =>
        new("transaction.explain", "error", "MCP tool invocation failed.", data);
}
