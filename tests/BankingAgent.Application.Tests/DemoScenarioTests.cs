using System.Text.Json;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BankingAgent.Application.Tests;

public sealed class DemoScenarioTests
{
    [Fact]
    public void Catalog_ContainsSixUniqueNonPiiScenarios()
    {
        Assert.Equal(6, DemoScenarioCatalog.All.Count);
        Assert.Equal(
            DemoScenarioCatalog.All.Count,
            DemoScenarioCatalog.All.Select(scenario => scenario.Id).Distinct().Count());
        Assert.Equal(
            [
                "approved-dispute",
                "hosted-agent-failure",
                "hosted-agent-timeout",
                "rejected-dispute",
                "suspicious-activity",
                "transaction-explanation"
            ],
            DemoScenarioCatalog.All.Select(scenario => scenario.Id).Order().ToArray());
        Assert.All(
            DemoScenarioCatalog.All,
            scenario =>
            {
                Assert.Contains("DEMO-TXN-", scenario.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("@", scenario.UserMessage, StringComparison.Ordinal);
                Assert.DoesNotContain("customer", scenario.UserMessage, StringComparison.OrdinalIgnoreCase);
            });
    }

    [Theory]
    [InlineData("transaction-explanation", WorkflowStatus.Completed)]
    [InlineData("suspicious-activity", WorkflowStatus.Completed)]
    [InlineData("approved-dispute", WorkflowStatus.WaitingForApproval)]
    [InlineData("rejected-dispute", WorkflowStatus.WaitingForApproval)]
    [InlineData("hosted-agent-failure", WorkflowStatus.Failed)]
    [InlineData("hosted-agent-timeout", WorkflowStatus.Failed)]
    public async Task StartDemoAsync_ProducesExpectedDurableState(
        string scenarioId,
        WorkflowStatus expectedStatus)
    {
        var fixture = new DemoFixture();

        var workflow = await fixture.Service.StartDemoAsync(scenarioId);

        Assert.Equal(expectedStatus, workflow.Status);
        Assert.Contains(
            workflow.Events,
            workflowEvent => workflowEvent.Type == "workflow.demo_scenario");
        Assert.Equal(workflow, await fixture.WorkflowRepository.GetAsync(workflow.Id));
        if (expectedStatus == WorkflowStatus.Failed)
        {
            Assert.Contains(
                workflow.Events,
                workflowEvent => workflowEvent.Type == "workflow.failed");
        }
    }

    [Theory]
    [InlineData("approved-dispute", "approve", WorkflowStatus.Completed, true)]
    [InlineData("rejected-dispute", "reject", WorkflowStatus.Rejected, false)]
    public async Task DisputeScenario_DecisionProducesExpectedCaseOutcome(
        string scenarioId,
        string decision,
        WorkflowStatus expectedStatus,
        bool expectsSupportCase)
    {
        var fixture = new DemoFixture();
        var workflow = await fixture.Service.StartDemoAsync(scenarioId);

        var decided = await fixture.Service.ApproveAsync(
            workflow.Id,
            decision,
            "Synthetic demo decision");

        Assert.Equal(expectedStatus, decided.Status);
        Assert.Equal(
            expectsSupportCase,
            await fixture.ActionRepository.GetSupportCaseAsync(workflow.Id) is not null);
    }

    [Fact]
    public async Task StartDemoAsync_RerunCreatesIndependentWorkflowsWithoutCleanup()
    {
        var fixture = new DemoFixture();

        var first = await fixture.Service.StartDemoAsync("transaction-explanation");
        var second = await fixture.Service.StartDemoAsync("transaction-explanation");

        Assert.NotEqual(first.Id, second.Id);
        Assert.NotEqual(first.TraceId, second.TraceId);
        Assert.Equal(2, fixture.WorkflowRepository.Count);
    }

    [Fact]
    public async Task StartDemoAsync_WhenDisabledRejectsScenario()
    {
        var fixture = new DemoFixture(demoScenariosEnabled: false);

        await Assert.ThrowsAsync<RequestValidationException>(
            () => fixture.Service.StartDemoAsync("transaction-explanation"));
    }

    private sealed class DemoFixture
    {
        public DemoFixture(bool demoScenariosEnabled = true)
        {
            WorkflowRepository = new InMemoryWorkflowRepository();
            ActionRepository = new InMemoryWorkflowActionRepository(WorkflowRepository);
            Service = new WorkflowService(
                new DeterministicAgentClient(),
                NullLogger<WorkflowService>.Instance,
                WorkflowRepository,
                ActionRepository,
                new DemoScenarioPolicy(demoScenariosEnabled));
        }

        public InMemoryWorkflowRepository WorkflowRepository { get; }
        public InMemoryWorkflowActionRepository ActionRepository { get; }
        public WorkflowService Service { get; }
    }

    private sealed class InMemoryWorkflowRepository : IWorkflowRepository
    {
        private readonly Dictionary<Guid, WorkflowState> _workflows = [];

        public int Count => _workflows.Count;

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

    private sealed class InMemoryWorkflowActionRepository(
        InMemoryWorkflowRepository workflowRepository) : IWorkflowActionRepository
    {
        private readonly Dictionary<Guid, SupportCase> _supportCases = [];

        public Task<SupportCase?> GetSupportCaseAsync(
            Guid workflowId,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(_supportCases.GetValueOrDefault(workflowId));

        public async Task RecordDecisionAsync(
            WorkflowState workflow,
            ApprovalDecision decision,
            ActionExecution? actionExecution,
            SupportCase? supportCase,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            await workflowRepository.UpdateAsync(
                workflow,
                expectedVersion,
                cancellationToken);
            if (supportCase is not null)
            {
                _supportCases.Add(workflow.Id, supportCase);
            }
        }
    }

    private sealed class DeterministicAgentClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            var userMessage = parameters["user_message"]?.ToString() ?? string.Empty;
            var route = WorkflowRoutingPolicy.Decide(userMessage);
            var agent = toolName == "workflow.plan" ? "workflow-planning" : route.Agent;
            var response = JsonSerializer.Serialize(new
            {
                agent,
                status = "ok",
                intent = route.Agent,
                summary = "Synthetic deterministic demo result.",
                requires_approval = route.RequiresApproval,
                selected_agent = toolName == "workflow.plan" ? route.Agent : null,
                evidence = Array.Empty<string>()
            });
            return Task.FromResult(new McpToolResult(
                toolName,
                "ok",
                "Synthetic deterministic demo result.",
                new Dictionary<string, object?>
                {
                    ["response_body"] = response
                }));
        }
    }
}
