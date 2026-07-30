using BankingAgent.Application;
using Xunit;

namespace BankingAgent.Domain.Tests;

/// <summary>
/// Unit tests for WorkflowRoutingPolicy.
/// Pure deterministic routing — no external dependencies.
/// </summary>
public sealed class WorkflowRoutingPolicyTests
{
    // ──────────────────────────────────────────────────────────────────
    // Dispute routing (requires approval)
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("I want to dispute this charge.")]
    [InlineData("Please initiate a chargeback for transaction #1234.")]
    [InlineData("I need to refund this charge.")]
    public void Decide_DisputeKeyword_ReturnDisputePlanningWithApproval(string message)
    {
        var route = WorkflowRoutingPolicy.Decide(message);

        Assert.Equal("dispute-planning", route.Agent);
        Assert.True(route.RequiresApproval);
    }

    // ──────────────────────────────────────────────────────────────────
    // Suspicious-activity routing
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("There is suspicious activity on my card.")]
    [InlineData("I think there is fraud on my account.")]
    [InlineData("This transaction is not mine — explain what I should review.")]
    [InlineData("This transaction is not my transaction.")]
    public void Decide_SuspiciousMessageWithoutSensitiveAction_ReturnsSuspiciousActivityWithoutApproval(string message)
    {
        var route = WorkflowRoutingPolicy.Decide(message);

        Assert.Equal("suspicious-activity", route.Agent);
        Assert.False(route.RequiresApproval);
    }

    [Theory]
    [InlineData("Freeze my card; this transaction is not mine.")]
    [InlineData("Block my account — I see suspicious activity.")]
    [InlineData("Close the account, this looks like fraud.")]
    public void Decide_SuspiciousMessageWithSensitiveAction_ReturnsSuspiciousActivityWithApproval(string message)
    {
        var route = WorkflowRoutingPolicy.Decide(message);

        Assert.Equal("suspicious-activity", route.Agent);
        Assert.True(route.RequiresApproval);
    }

    // ──────────────────────────────────────────────────────────────────
    // Default transaction-explanation routing (no approval)
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Why is this card transaction pending?")]
    [InlineData("What does this merchant charge mean?")]
    [InlineData("I'd like to understand my statement.")]
    public void Decide_TransactionMessage_ReturnsTransactionExplanationWithoutApproval(string message)
    {
        var route = WorkflowRoutingPolicy.Decide(message);

        Assert.Equal("transaction-explanation", route.Agent);
        Assert.False(route.RequiresApproval);
    }

    // ──────────────────────────────────────────────────────────────────
    // Dispute takes precedence over suspicious terms
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_DisputeAndSuspiciousTermsBoth_ReturnsDisputeRoute()
    {
        // dispute keyword is checked first in the policy
        var route = WorkflowRoutingPolicy.Decide("I want to dispute this fraud charge.");

        Assert.Equal("dispute-planning", route.Agent);
        Assert.True(route.RequiresApproval);
    }

    // ──────────────────────────────────────────────────────────────────
    // Case-insensitivity
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_UppercaseKeywords_RouteCorrectly()
    {
        Assert.Equal("dispute-planning", WorkflowRoutingPolicy.Decide("DISPUTE THIS CHARGE").Agent);
        Assert.Equal("suspicious-activity", WorkflowRoutingPolicy.Decide("FRAUD DETECTED").Agent);
        Assert.Equal("transaction-explanation", WorkflowRoutingPolicy.Decide("WHY IS THIS PENDING").Agent);
    }

    // ──────────────────────────────────────────────────────────────────
    // Guard: null / empty / whitespace throws
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_NullMessage_ThrowsArgumentNullException()
    {
        Assert.Throws<ArgumentNullException>(() => WorkflowRoutingPolicy.Decide(null!));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Decide_EmptyOrWhitespaceMessage_ThrowsArgumentException(string message)
    {
        Assert.Throws<ArgumentException>(() => WorkflowRoutingPolicy.Decide(message));
    }

    // ──────────────────────────────────────────────────────────────────
    // Determinism — same input always yields identical route
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void Decide_SameInputTwice_ReturnsSameRoute()
    {
        const string message = "Dispute this charge.";
        var r1 = WorkflowRoutingPolicy.Decide(message);
        var r2 = WorkflowRoutingPolicy.Decide(message);

        Assert.Equal(r1.Agent, r2.Agent);
        Assert.Equal(r1.RequiresApproval, r2.RequiresApproval);
    }
}
