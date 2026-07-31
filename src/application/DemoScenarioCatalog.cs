using BankingAgent.Domain;

namespace BankingAgent.Application;

public enum DemoScenarioFault
{
    None,
    HostedAgentFailure,
    HostedAgentTimeout
}

public sealed record DemoScenarioDefinition(
    string Id,
    string Title,
    string Description,
    string UserMessage,
    string ExpectedInitialStatus,
    string? ExpectedDecision,
    DemoScenarioFault Fault);

public static class DemoScenarioCatalog
{
    public static IReadOnlyList<DemoScenarioDefinition> All { get; } =
    [
        new(
            "transaction-explanation",
            "Explain a pending transaction",
            "Informational path with no approval.",
            "Explain why demo transaction DEMO-TXN-1002 at Metro Transit is pending.",
            WorkflowStatus.Completed.ToString(),
            null,
            DemoScenarioFault.None),
        new(
            "suspicious-activity",
            "Review suspicious activity",
            "Risk review that recommends safe next steps.",
            "Demo transaction DEMO-TXN-1003 at Alpine Digital is not mine. Explain what I should review.",
            WorkflowStatus.Completed.ToString(),
            null,
            DemoScenarioFault.None),
        new(
            "approved-dispute",
            "Approve a dispute",
            "Approval creates one simulated support case.",
            "Dispute demo transaction DEMO-TXN-1001 at Northwind Market.",
            WorkflowStatus.WaitingForApproval.ToString(),
            "approve",
            DemoScenarioFault.None),
        new(
            "rejected-dispute",
            "Reject a dispute",
            "Rejection records the decision without creating a case.",
            "Dispute demo transaction DEMO-TXN-1001 at Northwind Market.",
            WorkflowStatus.WaitingForApproval.ToString(),
            "reject",
            DemoScenarioFault.None),
        new(
            "hosted-agent-failure",
            "Simulate an agent failure",
            "Deterministic specialist failure and audit trail.",
            "Explain demo transaction DEMO-TXN-1002 at Metro Transit.",
            WorkflowStatus.Failed.ToString(),
            null,
            DemoScenarioFault.HostedAgentFailure),
        new(
            "hosted-agent-timeout",
            "Simulate an agent timeout",
            "Deterministic timeout and failure telemetry.",
            "Explain demo transaction DEMO-TXN-1002 at Metro Transit.",
            WorkflowStatus.Failed.ToString(),
            null,
            DemoScenarioFault.HostedAgentTimeout)
    ];

    private static readonly IReadOnlyDictionary<string, DemoScenarioDefinition> ById =
        All.ToDictionary(scenario => scenario.Id, StringComparer.OrdinalIgnoreCase);

    public static bool TryGet(string id, out DemoScenarioDefinition scenario) =>
        ById.TryGetValue(id, out scenario!);
}

public interface IDemoScenarioPolicy
{
    DemoScenarioDefinition Resolve(string scenarioId);
}

public sealed class DemoScenarioPolicy(bool enabled) : IDemoScenarioPolicy
{
    public static IDemoScenarioPolicy Disabled { get; } = new DemoScenarioPolicy(false);

    public DemoScenarioDefinition Resolve(string scenarioId)
    {
        if (!enabled)
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["demoScenario"] = ["Demo scenarios are not enabled in this environment."]
            });
        }

        if (!DemoScenarioCatalog.TryGet(scenarioId, out var scenario))
        {
            throw new RequestValidationException(new Dictionary<string, string[]>
            {
                ["demoScenario"] = ["Select a recognized demo scenario."]
            });
        }

        return scenario;
    }
}
