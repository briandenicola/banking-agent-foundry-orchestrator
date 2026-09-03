using BankingAgent.Application;
using BankingAgent.Infrastructure;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

/// <summary>
/// Foundry has been observed to accept scope-like request fields and silently
/// ignore them — an earlier probe passed a user identifier, received HTTP 200,
/// and got back the caller's own scope unchanged. If that happens with the
/// per-customer memory scope, one customer's remembered details would be handed
/// to another customer's workflow.
///
/// These tests pin the guard that makes that failure safe: memories returned
/// under a scope other than the one requested are discarded, so an ignored
/// scope costs personalisation rather than leaking data.
/// </summary>
public class CustomerProfileScopeTests
{
    private static ProfileReply ReplyWith(params ProfileMemory[] memories) =>
        new("some text", ["memory_search_call"], memories, memories.FirstOrDefault()?.Scope);

    [Fact]
    public void EnforceScope_keeps_memories_belonging_to_the_requested_scope()
    {
        var reply = ReplyWith(
            new ProfileMemory("user_profile", "prefers email contact", "customer-a"),
            new ProfileMemory("user_profile", "uses a screen reader", "customer-a"));

        var confined = CustomerProfileClient.EnforceScope(reply, "customer-a");

        Assert.Equal(2, confined.Memories.Count);
        Assert.Equal("customer-a", confined.Scope);
    }

    [Fact]
    public void EnforceScope_discards_memories_from_another_customer()
    {
        var reply = ReplyWith(
            new ProfileMemory("user_profile", "prefers email contact", "customer-a"),
            new ProfileMemory("user_profile", "banks with a joint account", "customer-b"));

        var confined = CustomerProfileClient.EnforceScope(reply, "customer-a");

        var content = Assert.Single(confined.Memories).Content;
        Assert.Equal("prefers email contact", content);
    }

    [Fact]
    public void EnforceScope_returns_nothing_when_the_service_ignored_the_scope()
    {
        // The silent-ignore case: the request asked for one customer and the
        // service answered from the shared caller scope.
        var reply = ReplyWith(
            new ProfileMemory("user_profile", "prefers email contact", "orchestrator-identity"));

        var confined = CustomerProfileClient.EnforceScope(reply, "customer-a");

        Assert.Empty(confined.Memories);
    }

    [Fact]
    public void EnforceScope_does_not_match_scopes_that_differ_only_by_case()
    {
        // Scopes are opaque identifiers, not display names. Treating two
        // different strings as the same customer is exactly the confusion this
        // guard exists to prevent.
        var reply = ReplyWith(new ProfileMemory("user_profile", "prefers email", "Customer-A"));

        var confined = CustomerProfileClient.EnforceScope(reply, "customer-a");

        Assert.Empty(confined.Memories);
    }

    [Fact]
    public void EnforceScope_reports_the_requested_scope_even_when_nothing_matched()
    {
        var reply = ReplyWith(new ProfileMemory("user_profile", "prefers email", "somebody-else"));

        var confined = CustomerProfileClient.EnforceScope(reply, "customer-a");

        Assert.Equal("customer-a", confined.Scope);
    }
}
