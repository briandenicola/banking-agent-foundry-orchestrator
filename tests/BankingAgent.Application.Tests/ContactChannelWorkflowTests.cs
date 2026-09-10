using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// Recall alone was never the point. A preference the workflow remembers but
/// cannot act on is worse than one it never read, because an agent that sees
/// "contact me by SMS" and has no reason to doubt it will say it will text.
///
/// These tests pin the seam where a remembered preference becomes an
/// instruction the specialist has to obey, and pin the boundary around it: what
/// the bank can deliver is a matter of wording and audit, and must not become a
/// reason to route differently or to demand a human.
/// </summary>
public sealed class ContactChannelWorkflowTests
{
    private readonly Mock<IMcpClient> _mcpClient = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowRepository> _repo = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowActionRepository> _actionRepo = new(MockBehavior.Loose);

    private const string UnavailableEventType = "workflow.contact_channel_unavailable";

    [Fact]
    public async Task The_specialist_is_told_what_to_do_about_an_unavailable_channel()
    {
        var run = await RunAsync("Prefers to be contacted by SMS only");

        var context = run.SpecialistContext;
        Assert.NotNull(context);
        Assert.Equal("sms", context!["contact_channel"]);
        Assert.Equal("known_unavailable", context["contact_channel_status"]);
        Assert.Contains("cannot send updates that way", (string)context["contact_channel_guidance"]!);
    }

    [Fact]
    public async Task The_preference_text_still_travels_alongside_the_instruction()
    {
        // The guidance says what to do; it deliberately does not restate the
        // preference. Dropping the original text would cost the agent the
        // customer's own words.
        var run = await RunAsync("Prefers to be contacted by SMS only");

        var preferences = Assert.IsAssignableFrom<IEnumerable<string>>(
            run.SpecialistContext!["customer_preferences"]);
        Assert.Contains("Prefers to be contacted by SMS only", preferences);
    }

    [Fact]
    public async Task A_customer_with_no_contact_preference_sees_no_change_at_all()
    {
        var run = await RunAsync("Needs large-print statements");

        Assert.DoesNotContain("contact_channel", run.SpecialistContext!.Keys);
        Assert.DoesNotContain("contact_channel_guidance", run.SpecialistContext.Keys);
        Assert.DoesNotContain(run.Workflow.Events, e => e.Type == UnavailableEventType);
    }

    [Fact]
    public async Task An_unmet_preference_is_recorded_on_the_timeline_not_just_said_in_the_answer()
    {
        var run = await RunAsync("Only contact me on TikTok");

        var unmet = Assert.Single(run.Workflow.Events, e => e.Type == UnavailableEventType);
        Assert.Contains("Cannot honour", unmet.Message);
        Assert.Contains("updates", unmet.Details, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task A_channel_the_service_can_deliver_is_not_recorded_as_a_failure()
    {
        var run = await RunAsync("Please contact me by secure message");

        Assert.Equal("supported", run.SpecialistContext!["contact_channel_status"]);
        Assert.DoesNotContain(run.Workflow.Events, e => e.Type == UnavailableEventType);
    }

    [Fact]
    public async Task What_the_bank_can_deliver_never_changes_whether_a_human_must_approve()
    {
        // The invariant that keeps this feature cosmetic where it should be
        // cosmetic. An unserviceable contact channel is a wording problem; it is
        // neither a reason to summon an approver nor a reason to stop needing
        // one. Both directions are checked, because a policy that only ever
        // escalates would still be wrong.
        var withoutPreference = await RunAsync("Needs large-print statements");
        var withUnavailable = await RunAsync("Only contact me on TikTok");

        Assert.Equal(withoutPreference.Workflow.RequiresApproval, withUnavailable.Workflow.RequiresApproval);
        Assert.Equal(withoutPreference.Workflow.Status, withUnavailable.Workflow.Status);
    }

    [Fact]
    public async Task What_the_bank_can_deliver_never_changes_which_agent_handles_the_request()
    {
        var withoutPreference = await RunAsync("Needs large-print statements");
        var withUnavailable = await RunAsync("Only contact me on TikTok");

        Assert.Equal(withoutPreference.InvokedTools, withUnavailable.InvokedTools);
    }

    private async Task<RunResult> RunAsync(string preference)
    {
        WorkflowState? persisted = null;
        var repo = new Mock<IWorkflowRepository>(MockBehavior.Loose);
        repo.Setup(r => r.AddAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, CancellationToken>((workflow, _) => persisted = workflow)
            .Returns(Task.CompletedTask);
        repo.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);
        repo.Setup(r => r.UpdateAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, long, CancellationToken>((workflow, _, _) => persisted = workflow)
            .Returns(Task.CompletedTask);

        _mcpClient.Setup(client => client.DiscoverToolsAsync(
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        Dictionary<string, object?>? specialistContext = null;
        var invokedTools = new List<string>();

        _mcpClient.Setup(client => client.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns((string toolName, IDictionary<string, object?> parameters, CancellationToken _) =>
            {
                invokedTools.Add(toolName);

                if (toolName != "workflow.plan"
                    && parameters.TryGetValue("context", out var raw)
                    && raw is Dictionary<string, object?> captured)
                {
                    specialistContext = new Dictionary<string, object?>(captured);
                }

                var agent = toolName == "workflow.plan" ? "workflow-planning" : "transaction-explanation";
                var body = JsonSerializer.Serialize(new
                {
                    agent,
                    status = "ok",
                    intent = "transaction_explanation",
                    summary = "Explaining the charge.",
                    requires_approval = false,
                    selected_agent = "transaction-explanation",
                    evidence = Array.Empty<string>(),
                    contract_version = "1.0",
                    execution_mode = "fallback"
                });

                return Task.FromResult(new McpToolResult(
                    toolName,
                    "ok",
                    "ok",
                    new Dictionary<string, object?> { ["response_body"] = body }));
            });

        var service = new WorkflowService(
            _mcpClient.Object,
            NullLogger<WorkflowService>.Instance,
            repo.Object,
            _actionRepo.Object,
            demoScenarioPolicy: null,
            loggerFactory: null,
            customerProfile: ProfileThatRecalls(preference));

        var draft = await service.StartForCustomerAsync("Why is this charge pending?", "customer-a");
        var completed = await service.RecoverAsync(draft.Id);

        return new RunResult(completed, specialistContext, invokedTools);
    }

    private static ICustomerProfileClient ProfileThatRecalls(params string[] preferences)
    {
        var profile = new Mock<ICustomerProfileClient>(MockBehavior.Loose);
        profile.SetupGet(client => client.IsConfigured).Returns(true);
        profile
            .Setup(client => client.AskAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileReply(
                "here is what I remember",
                [],
                [.. preferences.Select(p => new ProfileMemory("user_profile", p, "customer-a"))],
                "customer-a"));
        return profile.Object;
    }

    private sealed record RunResult(
        WorkflowState Workflow,
        Dictionary<string, object?>? SpecialistContext,
        IReadOnlyList<string> InvokedTools);
}
