using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BankingAgent.Application.Tests;

public sealed class WorkflowRoutingAuthorityTests
{
    [Fact]
    public async Task RecoverAsync_PlannerAgreesWithPolicy_PersistsRouteWithoutDisagreement()
    {
        var fixture = new RoutingFixture(new PlannerReply("dispute-planning", RequiresApproval: true));

        var result = await fixture.RunAsync("I want to dispute this charge.");

        Assert.Contains("dispute.plan", fixture.McpClient.InvokedTools);
        Assert.Contains(result.Events, workflowEvent => workflowEvent.Type == "workflow.route_selected");
        Assert.DoesNotContain(result.Events, workflowEvent => workflowEvent.Type == "workflow.route_disagreement");
        Assert.DoesNotContain(result.Events, workflowEvent => workflowEvent.Type == "workflow.route_fallback");
    }

    [Fact]
    public async Task RecoverAsync_PlannerDisagreesWithPolicy_PlannerAgentWinsAndPersistsDisagreementEvent()
    {
        var fixture = new RoutingFixture(new PlannerReply("suspicious-activity", RequiresApproval: false));

        var result = await fixture.RunAsync("Why is this card transaction pending?");

        Assert.Contains("suspicious.assess", fixture.McpClient.InvokedTools);
        Assert.DoesNotContain("transaction.explain", fixture.McpClient.InvokedTools);
        var disagreement = Assert.Single(result.Events, workflowEvent => workflowEvent.Type == "workflow.route_disagreement");
        using var details = JsonDocument.Parse(disagreement.Details!);
        Assert.Equal("suspicious-activity", details.RootElement.GetProperty("planner_agent").GetString());
        Assert.Equal("transaction-explanation", details.RootElement.GetProperty("policy_agent").GetString());
        Assert.Equal("suspicious-activity", details.RootElement.GetProperty("winning_agent").GetString());
        Assert.Equal("planner", details.RootElement.GetProperty("winner").GetString());
    }

    [Fact]
    public async Task RecoverAsync_PlannerReturnsNoSelectedAgent_FallsBackToPolicyAndPersistsFallbackEvent()
    {
        var fixture = new RoutingFixture(new PlannerReply(null, RequiresApproval: false));

        var result = await fixture.RunAsync("I want to dispute this charge.");

        Assert.Contains("dispute.plan", fixture.McpClient.InvokedTools);
        var fallback = Assert.Single(result.Events, workflowEvent => workflowEvent.Type == "workflow.route_fallback");
        Assert.Contains("no selected_agent", fallback.Message, StringComparison.OrdinalIgnoreCase);
        using var details = JsonDocument.Parse(fallback.Details!);
        Assert.Equal("missing_selected_agent", details.RootElement.GetProperty("reason_code").GetString());
        Assert.Equal("dispute-planning", details.RootElement.GetProperty("winning_agent").GetString());
    }

    [Fact]
    public async Task RecoverAsync_PlannerReturnsUnknownSelectedAgent_FallsBackToPolicyAndPersistsFallbackEvent()
    {
        var fixture = new RoutingFixture(new PlannerReply("made-up-agent", RequiresApproval: false));

        var result = await fixture.RunAsync("There is suspicious activity on my card.");

        Assert.Contains("suspicious.assess", fixture.McpClient.InvokedTools);
        var fallback = Assert.Single(result.Events, workflowEvent => workflowEvent.Type == "workflow.route_fallback");
        Assert.Contains("unrecognized selected_agent", fallback.Message, StringComparison.OrdinalIgnoreCase);
        using var details = JsonDocument.Parse(fallback.Details!);
        Assert.Equal("unknown_selected_agent", details.RootElement.GetProperty("reason_code").GetString());
        Assert.Equal("made-up-agent", details.RootElement.GetProperty("planner_agent").GetString());
        Assert.Equal("suspicious-activity", details.RootElement.GetProperty("winning_agent").GetString());
    }

    [Fact]
    public async Task RecoverAsync_PlannerOnlySuspiciousRouteWithoutKeywords_ReachesSuspiciousSpecialist()
    {
        var fixture = new RoutingFixture(new PlannerReply("suspicious-activity", RequiresApproval: false));

        var result = await fixture.RunAsync("I don't recognise this payment to ACME");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Contains("suspicious.assess", fixture.McpClient.InvokedTools);
        Assert.DoesNotContain("transaction.explain", fixture.McpClient.InvokedTools);
    }

    [Fact]
    public async Task RecoverAsync_PolicyEscalatesApprovalButCannotDeescalatePlannerApproval()
    {
        var escalationFixture = new RoutingFixture(new PlannerReply("suspicious-activity", RequiresApproval: false));
        var escalated = await escalationFixture.RunAsync("Freeze my card; this transaction is not mine.");

        Assert.Equal(WorkflowStatus.WaitingForApproval, escalated.Status);
        Assert.True(escalated.RequiresApproval);
        var escalationEvent = Assert.Single(
            escalated.Events,
            workflowEvent => workflowEvent.Type == "workflow.route_disagreement");
        using (var details = JsonDocument.Parse(escalationEvent.Details!))
        {
            Assert.False(details.RootElement.GetProperty("planner_requires_approval").GetBoolean());
            Assert.True(details.RootElement.GetProperty("policy_requires_approval").GetBoolean());
            Assert.True(details.RootElement.GetProperty("winning_requires_approval").GetBoolean());
        }

        var deescalationFixture = new RoutingFixture(new PlannerReply("transaction-explanation", RequiresApproval: true));
        var notDeescalated = await deescalationFixture.RunAsync("Why is this card transaction pending?");

        Assert.Equal(WorkflowStatus.WaitingForApproval, notDeescalated.Status);
        Assert.True(notDeescalated.RequiresApproval);
        var deescalationEvent = Assert.Single(
            notDeescalated.Events,
            workflowEvent => workflowEvent.Type == "workflow.route_disagreement");
        using var deescalationDetails = JsonDocument.Parse(deescalationEvent.Details!);
        Assert.True(deescalationDetails.RootElement.GetProperty("planner_requires_approval").GetBoolean());
        Assert.False(deescalationDetails.RootElement.GetProperty("policy_requires_approval").GetBoolean());
        Assert.True(deescalationDetails.RootElement.GetProperty("winning_requires_approval").GetBoolean());
    }

    private sealed record PlannerReply(string? SelectedAgent, bool RequiresApproval);

    private sealed class RoutingFixture(PlannerReply plannerReply)
    {
        private readonly InMemoryWorkflowRepository _workflowRepository = new();
        private readonly InMemoryWorkflowActionRepository _actionRepository = new();

        public ConfigurableMcpClient McpClient { get; } = new(plannerReply);

        public async Task<WorkflowState> RunAsync(string userMessage)
        {
            var service = new WorkflowService(
                McpClient,
                NullLogger<WorkflowService>.Instance,
                _workflowRepository,
                _actionRepository);

            var draft = await service.StartAsync(userMessage);
            return await service.RecoverAsync(draft.Id);
        }
    }

    private sealed class InMemoryWorkflowRepository : IWorkflowRepository
    {
        private readonly Dictionary<Guid, WorkflowState> _workflows = [];

        public Task AddAsync(
            WorkflowState workflow,
            CancellationToken cancellationToken = default)
        {
            _workflows.Add(workflow.Id, workflow);
            return Task.CompletedTask;
        }

        public Task<WorkflowState?> GetAsync(
            Guid workflowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_workflows.GetValueOrDefault(workflowId));

        public Task UpdateAsync(
            WorkflowState workflow,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            Assert.Equal(expectedVersion, _workflows[workflow.Id].Version);
            _workflows[workflow.Id] = workflow;
            return Task.CompletedTask;
        }
    }

    private sealed class InMemoryWorkflowActionRepository : IWorkflowActionRepository
    {
        public Task<SupportCase?> GetSupportCaseAsync(
            Guid workflowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<SupportCase?>(null);

        public Task RecordDecisionAsync(
            WorkflowState workflow,
            ApprovalDecision decision,
            ActionExecution? actionExecution,
            SupportCase? supportCase,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            Task.CompletedTask;
    }

    private sealed class ConfigurableMcpClient(PlannerReply plannerReply) : IMcpClient
    {
        public List<string> InvokedTools { get; } = [];

        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            InvokedTools.Add(toolName);
            var agent = toolName switch
            {
                "workflow.plan" => "workflow-planning",
                "transaction.explain" => "transaction-explanation",
                "suspicious.assess" => "suspicious-activity",
                "dispute.plan" => "dispute-planning",
                _ => throw new InvalidOperationException($"Unexpected tool {toolName}.")
            };
            var response = JsonSerializer.Serialize(new
            {
                agent,
                status = "ok",
                intent = agent,
                summary = $"{agent} summary",
                requires_approval = toolName == "workflow.plan" ? plannerReply.RequiresApproval : false,
                selected_agent = toolName == "workflow.plan" ? plannerReply.SelectedAgent : null,
                evidence = Array.Empty<string>(),
                contract_version = "1.0",
                execution_mode = "fallback"
            });
            return Task.FromResult(new McpToolResult(
                toolName,
                "ok",
                "ok",
                new Dictionary<string, object?> { ["response_body"] = response }));
        }

        public Task<IReadOnlyList<McpToolDefinition>> DiscoverToolsAsync(
            string? agentName = null,
            CancellationToken cancellationToken = default) =>
            Task.FromResult<IReadOnlyList<McpToolDefinition>>([]);
    }
}
