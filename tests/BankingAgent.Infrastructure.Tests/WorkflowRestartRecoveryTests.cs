using BankingAgent.Application;
using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;
using System.Text.Json;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class WorkflowRestartRecoveryTests : IAsyncLifetime
{
    private readonly string _databasePath =
        Path.Combine(Path.GetTempPath(), $"banking-agent-restart-{Guid.NewGuid():N}.db");

    [Fact]
    public async Task WorkflowAndApprovedAction_SurviveContextRestartWithoutDuplicates()
    {
        var workflow = BuildWaitingWorkflow();
        await using (var initialContext = CreateContext())
        {
            await initialContext.Database.EnsureCreatedAsync();
            await new EfWorkflowRepository(initialContext).AddAsync(workflow);
        }

        await using (var restartedContext = CreateContext())
        {
            var recovered = await new EfWorkflowRepository(restartedContext).GetAsync(workflow.Id);
            Assert.NotNull(recovered);
            Assert.Equal(workflow.Id, recovered.Id);
            Assert.Equal(workflow.TraceId, recovered.TraceId);
            Assert.Equal(workflow.Status, recovered.Status);
            Assert.Equal(workflow.Version, recovered.Version);
            Assert.Equal(workflow.Events, recovered.Events);
        }

        await using (var approvalContext = CreateContext())
        {
            var service = CreateService(approvalContext);
            var approved = await service.ApproveAsync(
                workflow.Id,
                "approve",
                "Synthetic restart approval");
            Assert.Equal(WorkflowStatus.Completed, approved.Status);
        }

        await using (var secondRestartContext = CreateContext())
        {
            var service = CreateService(secondRestartContext);
            var recovered = await service.GetAsync(workflow.Id);
            var supportCase = await service.GetSupportCaseAsync(workflow.Id);

            Assert.NotNull(recovered);
            Assert.Equal(WorkflowStatus.Completed, recovered.Status);
            Assert.Equal("approve", recovered.ApprovalDecision);
            Assert.NotNull(supportCase);

            var retried = await service.ApproveAsync(
                workflow.Id,
                "approve",
                "Synthetic duplicate approval");
            Assert.Equal(recovered.Id, retried.Id);
            Assert.Equal(recovered.Status, retried.Status);
            Assert.Equal(recovered.ApprovalDecision, retried.ApprovalDecision);
            Assert.Equal(recovered.Version, retried.Version);
            Assert.Equal(
                1,
                await secondRestartContext.SupportCases.CountAsync(
                    item => item.WorkflowId == workflow.Id));
            Assert.Equal(
                1,
                await secondRestartContext.ActionExecutions.CountAsync(
                    item => item.WorkflowId == workflow.Id));
            Assert.Equal(
                1,
                await secondRestartContext.ApprovalDecisions.CountAsync(
                    item => item.WorkflowId == workflow.Id));
        }
    }

    [Fact]
    public async Task CompetingRecoveryClaimers_OnlyOneClaimsStaleWorkflow()
    {
        var workflow = BuildDraftWorkflow(DateTimeOffset.UtcNow.AddMinutes(-5));
        await using (var initialContext = CreateContext())
        {
            await initialContext.Database.EnsureCreatedAsync();
            await new EfWorkflowRepository(initialContext).AddAsync(workflow);
        }

        await using var firstContext = CreateContext();
        await using var secondContext = CreateContext();
        var staleBefore = DateTimeOffset.UtcNow.AddMinutes(-2);
        var claimedAt = DateTimeOffset.UtcNow;

        var claims = await Task.WhenAll(
            new EfWorkflowRepository(firstContext).ClaimNextAsync(staleBefore, claimedAt),
            new EfWorkflowRepository(secondContext).ClaimNextAsync(staleBefore, claimedAt));

        var claimed = Assert.Single(claims, item => item is not null);
        Assert.Equal(workflow.Id, claimed!.Id);
        Assert.Equal(WorkflowStatus.Recovering, claimed.Status);

        await using var verificationContext = CreateContext();
        var persisted = await new EfWorkflowRepository(verificationContext).GetAsync(workflow.Id);
        Assert.NotNull(persisted);
        Assert.Equal(WorkflowStatus.Recovering, persisted.Status);
        Assert.Single(
            persisted.Events,
            workflowEvent => workflowEvent.Type == "workflow.recovery_claimed");
    }

    [Fact]
    public async Task StaleDraft_ResumesToTerminalStateAfterRestart()
    {
        var workflow = BuildDraftWorkflow(DateTimeOffset.UtcNow.AddMinutes(-5));
        await using (var initialContext = CreateContext())
        {
            await initialContext.Database.EnsureCreatedAsync();
            await new EfWorkflowRepository(initialContext).AddAsync(workflow);
        }

        await using (var claimingContext = CreateContext())
        {
            var claimed = await new EfWorkflowRepository(claimingContext).ClaimNextAsync(
                DateTimeOffset.UtcNow.AddMinutes(-2),
                DateTimeOffset.UtcNow);
            Assert.NotNull(claimed);
        }

        await using (var restartedContext = CreateContext())
        {
            var service = new WorkflowService(
                new DeterministicMcpClient(),
                NullLogger<WorkflowService>.Instance,
                new EfWorkflowRepository(restartedContext),
                new EfWorkflowActionRepository(restartedContext));

            var recovered = await service.RecoverAsync(workflow.Id);

            Assert.Equal(WorkflowStatus.Completed, recovered.Status);
            Assert.Contains(
                recovered.Events,
                workflowEvent => workflowEvent.Type == "workflow.recovery_claimed");
            Assert.Contains(
                recovered.Events,
                workflowEvent => workflowEvent.Type == "workflow.completed");
        }
    }

    private BankingAgentDbContext CreateContext()
    {
        var connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate
        }.ToString();
        var options = new DbContextOptionsBuilder<BankingAgentDbContext>()
            .UseSqlite(connectionString)
            .Options;
        return new BankingAgentDbContext(options);
    }

    private static WorkflowService CreateService(BankingAgentDbContext context) =>
        new(
            new UnusedMcpClient(),
            NullLogger<WorkflowService>.Instance,
            new EfWorkflowRepository(context),
            new EfWorkflowActionRepository(context));

    private static WorkflowState BuildWaitingWorkflow()
    {
        var now = DateTimeOffset.UtcNow;
        return new WorkflowState(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            "Dispute demo transaction DEMO-TXN-1001.",
            WorkflowStatus.WaitingForApproval,
            "dispute",
            true,
            null,
            null,
            now,
            now,
            [new WorkflowEvent("workflow.started", "Workflow started", now, "system")],
            1);
    }

    private static WorkflowState BuildDraftWorkflow(DateTimeOffset updatedAt) =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid().ToString("N"),
            "Explain demo transaction DEMO-TXN-1001.",
            WorkflowStatus.Draft,
            null,
            false,
            null,
            null,
            updatedAt,
            updatedAt,
            [new WorkflowEvent("workflow.started", "Workflow started", updatedAt, "system")],
            0);

    public Task InitializeAsync() => Task.CompletedTask;

    public Task DisposeAsync()
    {
        if (File.Exists(_databasePath))
        {
            File.Delete(_databasePath);
        }

        return Task.CompletedTask;
    }

    private sealed class UnusedMcpClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Agent invocation is not used during recovery approval.");
    }

    private sealed class DeterministicMcpClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default)
        {
            var selectedAgent = "transaction-explanation";
            var agent = toolName == "workflow.plan" ? "workflow-planning" : selectedAgent;
            var response = JsonSerializer.Serialize(new
            {
                agent,
                status = "ok",
                intent = selectedAgent,
                summary = "Synthetic recovery result.",
                requires_approval = false,
                selected_agent = toolName == "workflow.plan" ? selectedAgent : null,
                evidence = Array.Empty<string>()
            });
            return Task.FromResult(new McpToolResult(
                toolName,
                "ok",
                "Synthetic recovery result.",
                new Dictionary<string, object?> { ["response_body"] = response }));
        }
    }
}
