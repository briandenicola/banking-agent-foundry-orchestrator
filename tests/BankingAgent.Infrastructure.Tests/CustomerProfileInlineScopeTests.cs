using System.Text.Json.Nodes;
using BankingAgent.Infrastructure;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

/// <summary>
/// Per-customer memory is only possible by sending the agent definition inline:
/// a scope passed next to an agent reference is accepted and silently ignored,
/// which <c>scripts/verify-memory-scope.py</c> demonstrates against the live
/// service. These tests pin the request that rewrite produces.
/// </summary>
public class CustomerProfileInlineScopeTests
{
    private static JsonObject Definition(string tools = """
        [
          {"type": "memory_search_preview", "memory_store_name": "store", "scope": "{{$userId}}", "update_delay": 0},
          {"type": "code_interpreter"}
        ]
        """) =>
        JsonNode.Parse($$"""
        {
          "kind": "prompt",
          "model": "gpt-5.4-mini",
          "instructions": "You are a retail banking servicing assistant.",
          "tools": {{tools}}
        }
        """)!.AsObject();

    private static JsonArray Tools(JsonObject request) => request["tools"]!.AsArray();

    private static JsonObject Tool(JsonObject request, string type) =>
        Tools(request).Select(tool => tool!.AsObject())
            .Single(tool => tool["type"]!.GetValue<string>() == type);

    [Fact]
    public void BuildScopedRequest_binds_the_memory_tool_to_the_requested_scope()
    {
        var request = CustomerProfileClient.BuildScopedRequest(Definition(), "customer-a", "hello");

        Assert.Equal("customer-a", Tool(request, "memory_search_preview")["scope"]!.GetValue<string>());
        Assert.Equal("hello", request["input"]!.GetValue<string>());
    }

    [Fact]
    public void BuildScopedRequest_keeps_the_code_interpreter_and_gives_it_a_container()
    {
        // Deployed, code_interpreter needs no container. Sent inline the API
        // rejects it outright without one:
        //   "Missing required parameter: 'tools[1].container'"
        // Dropping the tool to dodge that would quietly remove the agent's
        // ability to compute, which is half of what it exists to show.
        var request = CustomerProfileClient.BuildScopedRequest(Definition(), "customer-a", "hello");

        var container = Tool(request, "code_interpreter")["container"]!.AsObject();
        Assert.Equal("auto", container["type"]!.GetValue<string>());
        Assert.Equal(2, Tools(request).Count);
    }

    [Fact]
    public void BuildScopedRequest_leaves_an_explicit_container_alone()
    {
        var definition = Definition("""
            [
              {"type": "memory_search_preview", "scope": "{{$userId}}"},
              {"type": "code_interpreter", "container": {"type": "explicit"}}
            ]
            """);

        var request = CustomerProfileClient.BuildScopedRequest(definition, "customer-a", "hello");

        var container = Tool(request, "code_interpreter")["container"]!.AsObject();
        Assert.Equal("explicit", container["type"]!.GetValue<string>());
    }

    [Fact]
    public void BuildScopedRequest_carries_unrecognised_tools_across_untouched()
    {
        var definition = Definition("""
            [
              {"type": "memory_search_preview", "scope": "{{$userId}}"},
              {"type": "toolbox_search", "toolbox_name": "banking-toolbox"}
            ]
            """);

        var request = CustomerProfileClient.BuildScopedRequest(definition, "customer-a", "hello");

        Assert.Equal("banking-toolbox", Tool(request, "toolbox_search")["toolbox_name"]!.GetValue<string>());
    }

    [Fact]
    public void BuildScopedRequest_preserves_the_deployed_model_and_instructions()
    {
        // Terraform owns these. Restating them here would let the deployed
        // agent and the scoped request drift apart without anything noticing.
        var request = CustomerProfileClient.BuildScopedRequest(Definition(), "customer-a", "hello");

        Assert.Equal("gpt-5.4-mini", request["model"]!.GetValue<string>());
        Assert.Equal(
            "You are a retail banking servicing assistant.",
            request["instructions"]!.GetValue<string>());
    }

    [Fact]
    public void BuildScopedRequest_drops_the_stored_definition_kind()
    {
        Assert.Null(CustomerProfileClient.BuildScopedRequest(Definition(), "customer-a", "hello")["kind"]);
    }

    [Fact]
    public void BuildScopedRequest_does_not_mutate_the_definition_it_was_given()
    {
        // The definition is fetched once and cached, so mutating it in place
        // would let the first customer's scope persist into every later
        // request -- the exact cross-customer leak this design prevents.
        var definition = Definition();

        CustomerProfileClient.BuildScopedRequest(definition, "customer-a", "hello");
        var second = CustomerProfileClient.BuildScopedRequest(definition, "customer-b", "hello");

        Assert.Equal("{{$userId}}", Tool(definition, "memory_search_preview")["scope"]!.GetValue<string>());
        Assert.Equal("customer-b", Tool(second, "memory_search_preview")["scope"]!.GetValue<string>());
        Assert.Null(Tool(definition, "code_interpreter")["container"]);
    }

    [Theory]
    [InlineData("""[{"type": "code_interpreter"}]""")]
    [InlineData("[]")]
    public void BuildScopedRequest_refuses_a_definition_with_no_memory_tool(string tools)
    {
        // Sending it anyway would write the customer's turn into the shared
        // caller scope. Failing the request is the safe reading.
        Assert.Throws<CustomerProfileException>(
            () => CustomerProfileClient.BuildScopedRequest(Definition(tools), "customer-a", "hello"));
    }

    [Fact]
    public void ReadLatestDefinition_takes_the_highest_version_not_the_last_listed()
    {
        var body = """
        {"data": [
          {"version": "3", "definition": {"model": "newest", "tools": []}},
          {"version": "1", "definition": {"model": "oldest", "tools": []}}
        ]}
        """;

        Assert.Equal("newest", CustomerProfileClient.ReadLatestDefinition(body)["model"]!.GetValue<string>());
    }

    [Fact]
    public void ReadLatestDefinition_accepts_numeric_versions_and_a_value_envelope()
    {
        var body = """
        {"value": [
          {"version": 2, "definition": {"model": "newest", "tools": []}},
          {"version": 1, "definition": {"model": "oldest", "tools": []}}
        ]}
        """;

        Assert.Equal("newest", CustomerProfileClient.ReadLatestDefinition(body)["model"]!.GetValue<string>());
    }

    [Theory]
    [InlineData("""{"data": []}""")]
    [InlineData("""{}""")]
    [InlineData("""{"data": [{"version": "1"}]}""")]
    public void ReadLatestDefinition_fails_rather_than_inventing_a_definition(string body)
    {
        Assert.Throws<CustomerProfileException>(() => CustomerProfileClient.ReadLatestDefinition(body));
    }
}
