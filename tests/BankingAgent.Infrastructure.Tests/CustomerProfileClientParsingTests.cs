using BankingAgent.Infrastructure;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

/// <summary>
/// The profile page's central claim is that the memories it lists come from the
/// memory tool rather than from the model's prose. These tests pin that: the
/// parser must read <c>memory_search_call</c> output and must not fall back to
/// the assistant message when the tool returns nothing.
/// </summary>
public class CustomerProfileClientParsingTests
{
    private const string ResponseWithMemories = """
    {
      "output": [
        {
          "type": "memory_search_call",
          "memories": [
            {
              "memory_id": "m1",
              "kind": "user_profile",
              "content": "Prefers SMS only; needs large-print statements.",
              "scope": "tenant_user"
            }
          ]
        },
        {
          "type": "message",
          "content": [{ "type": "output_text", "text": "I will contact you by SMS." }]
        }
      ]
    }
    """;

    [Fact]
    public void Parse_ReadsMemoriesFromTheToolCallRatherThanTheMessage()
    {
        var reply = CustomerProfileClient.Parse(ResponseWithMemories);

        var memory = Assert.Single(reply.Memories);
        Assert.Equal("user_profile", memory.Kind);
        Assert.Equal("Prefers SMS only; needs large-print statements.", memory.Content);
        Assert.Equal("tenant_user", reply.Scope);
    }

    [Fact]
    public void Parse_ReportsTheToolsFoundryRanButNotTheMessage()
    {
        var reply = CustomerProfileClient.Parse(ResponseWithMemories);

        // "message" is the reply, not a tool; listing it would overstate what ran.
        Assert.Equal(["memory_search_call"], reply.ToolsUsed);
    }

    [Fact]
    public void Parse_ReturnsTheAssistantText()
    {
        var reply = CustomerProfileClient.Parse(ResponseWithMemories);

        Assert.Equal("I will contact you by SMS.", reply.Text);
    }

    [Fact]
    public void Parse_ReportsNoMemoriesWhenTheStoreIsEmpty()
    {
        // A cleared store still answers, and the page must not imply otherwise.
        const string body = """
        {
          "output": [
            { "type": "memory_search_call", "memories": [] },
            { "type": "message", "content": [{ "text": "I do not know you yet." }] }
          ]
        }
        """;

        var reply = CustomerProfileClient.Parse(body);

        Assert.Empty(reply.Memories);
        Assert.Null(reply.Scope);
        Assert.Equal("I do not know you yet.", reply.Text);
    }

    [Fact]
    public void Parse_RecordsTheCodeInterpreterWhenFoundryRunsIt()
    {
        const string body = """
        {
          "output": [
            { "type": "code_interpreter_call" },
            { "type": "memory_search_call", "memories": [] },
            { "type": "message", "content": [{ "text": "The total is 957.77." }] }
          ]
        }
        """;

        var reply = CustomerProfileClient.Parse(body);

        Assert.Contains("code_interpreter_call", reply.ToolsUsed);
    }

    [Fact]
    public void Parse_ToleratesAResponseWithNoOutput()
    {
        var reply = CustomerProfileClient.Parse("""{ "id": "resp_1" }""");

        Assert.Equal(string.Empty, reply.Text);
        Assert.Empty(reply.Memories);
        Assert.Empty(reply.ToolsUsed);
    }
}
