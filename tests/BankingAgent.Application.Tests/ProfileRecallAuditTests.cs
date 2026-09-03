using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// Recalled preferences reach the planner and the specialist inside the model
/// prompt, where nothing observable records that they were used. These tests
/// pin the audit event that makes the recall visible, so a personalised answer
/// can be traced back to what personalised it.
///
/// They also pin the silence: the profile step is fail-open and runs ahead of
/// every workflow, so its non-events must not litter the trail of customers who
/// have no stored preferences.
/// </summary>
public sealed class ProfileRecallAuditTests
{
    private readonly Mock<IMcpClient> _mcpClient = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowRepository> _repo = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowActionRepository> _actionRepo = new(MockBehavior.Loose);

    private const string RecallEventType = "workflow.profile_recalled";

    [Fact]
    public async Task Recalled_preferences_are_recorded_on_the_workflow_timeline()
    {
        var completed = await RunWorkflowAsync(
            ProfileThatRecalls("Prefers SMS only", "Needs large-print statements"));

        var recall = completed.Events.Single(e => e.Type == RecallEventType);
        Assert.Equal("Recalled 2 remembered preferences", recall.Message);
        Assert.Contains("Prefers SMS only", recall.Details);
        Assert.Contains("Needs large-print statements", recall.Details);
    }

    [Fact]
    public async Task The_recall_event_is_attributed_to_the_profile_agent()
    {
        // The trail distinguishes what the bank's own system did from what an
        // agent contributed, so this must not be recorded as "system".
        var completed = await RunWorkflowAsync(ProfileThatRecalls("Prefers SMS only"));

        var recall = completed.Events.Single(e => e.Type == RecallEventType);
        Assert.Equal("customer-profile", recall.Actor);
    }

    [Fact]
    public async Task A_single_preference_is_described_in_the_singular()
    {
        var completed = await RunWorkflowAsync(ProfileThatRecalls("Prefers SMS only"));

        var recall = completed.Events.Single(e => e.Type == RecallEventType);
        Assert.Equal("Recalled 1 remembered preference", recall.Message);
    }

    [Fact]
    public async Task The_recall_is_stamped_when_memory_was_read_not_when_the_trail_was_written()
    {
        // Every event in a batch is stamped as it is assembled, which happens
        // only after the planner returns. Reusing that instant would date the
        // recall to after the plan it informed. The planner is delayed here so
        // that an assembly-time stamp is measurably wrong rather than merely
        // ordered correctly by luck of construction order.
        var completed = await RunWorkflowAsync(
            ProfileThatRecalls("Prefers SMS only"),
            plannerDelay: TimeSpan.FromMilliseconds(250));

        var recall = completed.Events.Single(e => e.Type == RecallEventType);
        var planner = completed.Events.Single(e => e.Type == "workflow.plan");
        Assert.True(
            planner.Timestamp - recall.Timestamp >= TimeSpan.FromMilliseconds(200),
            $"recall at {recall.Timestamp:O} should predate the planner at {planner.Timestamp:O} "
                + "by roughly the time the planner took");
    }

    [Fact]
    public async Task Nothing_is_recorded_when_the_customer_has_no_stored_preferences()
    {
        var profile = new Mock<ICustomerProfileClient>(MockBehavior.Loose);
        profile.SetupGet(client => client.IsConfigured).Returns(true);
        profile
            .Setup(client => client.AskAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ProfileReply("nothing stored", [], [], "customer-a"));

        var completed = await RunWorkflowAsync(profile.Object);

        Assert.DoesNotContain(completed.Events, e => e.Type == RecallEventType);
    }

    [Fact]
    public async Task Nothing_is_recorded_when_the_profile_lookup_fails()
    {
        // Fail-open means the workflow proceeds unpersonalised. Recording a
        // recall event here would claim preferences were applied when none were.
        var profile = new Mock<ICustomerProfileClient>(MockBehavior.Loose);
        profile.SetupGet(client => client.IsConfigured).Returns(true);
        profile
            .Setup(client => client.AskAsync(
                It.IsAny<string>(),
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("memory service unavailable"));

        var completed = await RunWorkflowAsync(profile.Object);

        Assert.NotEqual(WorkflowStatus.Failed, completed.Status);
        Assert.DoesNotContain(completed.Events, e => e.Type == RecallEventType);
    }

    [Fact]
    public async Task Nothing_is_recorded_without_an_identified_customer()
    {
        var completed = await RunWorkflowAsync(
            ProfileThatRecalls("Prefers SMS only"),
            customerId: null);

        Assert.DoesNotContain(completed.Events, e => e.Type == RecallEventType);
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

    private async Task<WorkflowState> RunWorkflowAsync(
        ICustomerProfileClient? customerProfile,
        string? customerId = "customer-a",
        TimeSpan plannerDelay = default)
    {
        WorkflowState? persisted = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, CancellationToken>((workflow, _) => persisted = workflow)
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.GetAsync(It.IsAny<Guid>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);
        _repo.Setup(r => r.UpdateAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, long, CancellationToken>((workflow, _, _) => persisted = workflow)
            .Returns(Task.CompletedTask);

        _mcpClient.Setup(client => client.DiscoverToolsAsync(
                It.IsAny<string?>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync([]);

        static string ReplyFor(string toolName)
        {
            var agent = toolName == "workflow.plan" ? "workflow-planning" : "transaction-explanation";
            return System.Text.Json.JsonSerializer.Serialize(new
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
        }

        _mcpClient.Setup(client => client.InvokeAsync(
                It.IsAny<string>(),
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .Returns(async (string toolName, IDictionary<string, object?> _, CancellationToken _) =>
            {
                if (toolName == "workflow.plan" && plannerDelay > TimeSpan.Zero)
                {
                    await Task.Delay(plannerDelay);
                }

                return new McpToolResult(
                    toolName,
                    "ok",
                    "ok",
                    new Dictionary<string, object?> { ["response_body"] = ReplyFor(toolName) });
            });

        var service = new WorkflowService(
            _mcpClient.Object,
            NullLogger<WorkflowService>.Instance,
            _repo.Object,
            _actionRepo.Object,
            demoScenarioPolicy: null,
            loggerFactory: null,
            customerProfile: customerProfile);

        var draft = await service.StartForCustomerAsync("Why is this charge pending?", customerId);
        return await service.RecoverAsync(draft.Id);
    }
}
