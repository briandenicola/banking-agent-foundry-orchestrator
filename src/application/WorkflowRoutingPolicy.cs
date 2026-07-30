namespace BankingAgent.Application;

public static class WorkflowRoutingPolicy
{
    private static readonly string[] DisputeTerms =
    [
        "dispute",
        "chargeback",
        "refund this charge"
    ];

    private static readonly string[] SuspiciousTerms =
    [
        "fraud",
        "suspicious",
        "not my transaction",
        "not mine"
    ];

    private static readonly string[] SensitiveActionTerms =
    [
        "freeze",
        "block",
        "close"
    ];

    public static WorkflowRoute Decide(string userMessage)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(userMessage);

        if (ContainsAny(userMessage, DisputeTerms))
        {
            return new WorkflowRoute("dispute-planning", RequiresApproval: true);
        }

        if (ContainsAny(userMessage, SuspiciousTerms))
        {
            return new WorkflowRoute(
                "suspicious-activity",
                RequiresApproval: ContainsAny(userMessage, SensitiveActionTerms));
        }

        return new WorkflowRoute("transaction-explanation", RequiresApproval: false);
    }

    private static bool ContainsAny(string value, IEnumerable<string> terms) =>
        terms.Any(term => value.Contains(term, StringComparison.OrdinalIgnoreCase));
}

public sealed record WorkflowRoute(string Agent, bool RequiresApproval);
