using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence;
using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.Data.Sqlite;
using Microsoft.EntityFrameworkCore;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class EfWorkflowActionRepositoryTests
{
    [Fact]
    public async Task RecordDecisionAsync_ApprovedDispute_PersistsDecisionActionCaseAndEvents()
    {
        await using var database = await TestDatabase.CreateAsync();
        var workflow = await database.SeedWorkflowAsync();
        var (updated, decision, action, supportCase) = BuildApproval(workflow);

        await new EfWorkflowActionRepository(database.Context).RecordDecisionAsync(
            updated,
            decision,
            action,
            supportCase,
            workflow.Version);

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.Workflows.SingleAsync(item => item.Id == workflow.Id);
        Assert.Equal(WorkflowStatus.Completed, persisted.Status);
        Assert.Equal("approve", persisted.ApprovalDecision);
        Assert.Equal(workflow.Version + 1, persisted.Version);
        Assert.Single(await database.Context.ApprovalDecisions.ToListAsync());
        Assert.Single(await database.Context.ActionExecutions.ToListAsync());
        Assert.Single(await database.Context.SupportCases.ToListAsync());
        Assert.Equal(3, await database.Context.WorkflowEvents.CountAsync());
    }

    [Fact]
    public async Task RecordDecisionAsync_Rejection_PersistsNoActionOrSupportCase()
    {
        await using var database = await TestDatabase.CreateAsync();
        var workflow = await database.SeedWorkflowAsync();
        var now = DateTimeOffset.UtcNow;
        var rejected = workflow with
        {
            Status = WorkflowStatus.Rejected,
            ApprovalDecision = "reject",
            ApprovalReason = "Not approved",
            UpdatedAt = now,
            Events = workflow.Events.Append(
                new WorkflowEvent("workflow.approval", "Approval reject", now, "user", "Not approved")).ToList(),
            Version = workflow.Version + 1
        };
        var decision = new ApprovalDecision(
            Guid.NewGuid(),
            workflow.Id,
            "reject",
            "Not approved",
            "user",
            now);

        await new EfWorkflowActionRepository(database.Context).RecordDecisionAsync(
            rejected,
            decision,
            null,
            null,
            workflow.Version);

        Assert.Empty(await database.Context.ActionExecutions.ToListAsync());
        Assert.Empty(await database.Context.SupportCases.ToListAsync());
        Assert.Single(await database.Context.ApprovalDecisions.ToListAsync());
    }

    [Fact]
    public async Task RecordDecisionAsync_RetryFromStaleSnapshot_IsIdempotent()
    {
        await using var database = await TestDatabase.CreateAsync();
        var workflow = await database.SeedWorkflowAsync();
        var first = BuildApproval(workflow);
        var staleRetry = BuildApproval(workflow);
        var repository = new EfWorkflowActionRepository(database.Context);

        await repository.RecordDecisionAsync(
            first.Updated,
            first.Decision,
            first.Action,
            first.SupportCase,
            workflow.Version);
        await repository.RecordDecisionAsync(
            staleRetry.Updated,
            staleRetry.Decision,
            staleRetry.Action,
            staleRetry.SupportCase,
            workflow.Version);

        Assert.Single(await database.Context.ApprovalDecisions.ToListAsync());
        Assert.Single(await database.Context.ActionExecutions.ToListAsync());
        Assert.Single(await database.Context.SupportCases.ToListAsync());
    }

    [Fact]
    public async Task RecordDecisionAsync_FailedSupportCaseInsert_RollsBackEntireDecision()
    {
        await using var database = await TestDatabase.CreateAsync();
        var workflow = await database.SeedWorkflowAsync();
        var otherWorkflow = await database.SeedWorkflowAsync();
        var approval = BuildApproval(workflow);
        database.Context.SupportCases.Add(new SupportCaseEntity
        {
            Id = Guid.NewGuid(),
            WorkflowId = otherWorkflow.Id,
            CaseNumber = approval.SupportCase.CaseNumber,
            Status = "Open",
            Summary = "Existing case",
            CreatedAt = DateTimeOffset.UtcNow,
            UpdatedAt = DateTimeOffset.UtcNow
        });
        await database.Context.SaveChangesAsync();
        database.Context.ChangeTracker.Clear();

        await Assert.ThrowsAsync<DbUpdateException>(() =>
            new EfWorkflowActionRepository(database.Context).RecordDecisionAsync(
                approval.Updated,
                approval.Decision,
                approval.Action,
                approval.SupportCase,
                workflow.Version));

        database.Context.ChangeTracker.Clear();
        var persisted = await database.Context.Workflows.SingleAsync(item => item.Id == workflow.Id);
        Assert.Equal(WorkflowStatus.WaitingForApproval, persisted.Status);
        Assert.Null(persisted.ApprovalDecision);
        Assert.Equal(workflow.Version, persisted.Version);
        Assert.Empty(await database.Context.ApprovalDecisions.Where(item => item.WorkflowId == workflow.Id).ToListAsync());
        Assert.Empty(await database.Context.ActionExecutions.Where(item => item.WorkflowId == workflow.Id).ToListAsync());
        Assert.Empty(await database.Context.SupportCases.Where(item => item.WorkflowId == workflow.Id).ToListAsync());
        Assert.Single(await database.Context.WorkflowEvents.Where(item => item.WorkflowId == workflow.Id).ToListAsync());
    }

    private static ApprovalData BuildApproval(WorkflowState workflow)
    {
        var now = DateTimeOffset.UtcNow;
        var supportCase = new SupportCase(
            Guid.NewGuid(),
            workflow.Id,
            $"DSP-{workflow.Id:N}",
            "Open",
            "Simulated support case for an approved transaction dispute.",
            now,
            now);
        var action = new ActionExecution(
            Guid.NewGuid(),
            workflow.Id,
            "dispute.support_case.create",
            $"dispute-support-case:{workflow.Id:N}",
            ActionExecutionStatus.Completed,
            now,
            now,
            """{"status":"Open"}""");
        var decision = new ApprovalDecision(
            Guid.NewGuid(),
            workflow.Id,
            "approve",
            "Approved",
            "user",
            now);
        var updated = workflow with
        {
            Status = WorkflowStatus.Completed,
            ApprovalDecision = "approve",
            ApprovalReason = "Approved",
            UpdatedAt = now,
            Events = workflow.Events.Concat(
            [
                new WorkflowEvent("workflow.approval", "Approval approve", now, "user", "Approved"),
                new WorkflowEvent("workflow.action_completed", "Support case created", now, "system", supportCase.CaseNumber)
            ]).ToList(),
            Version = workflow.Version + 1
        };
        return new ApprovalData(updated, decision, action, supportCase);
    }

    private sealed record ApprovalData(
        WorkflowState Updated,
        ApprovalDecision Decision,
        ActionExecution Action,
        SupportCase SupportCase);

    private sealed class TestDatabase : IAsyncDisposable
    {
        private readonly SqliteConnection _connection;

        private TestDatabase(SqliteConnection connection, BankingAgentDbContext context)
        {
            _connection = connection;
            Context = context;
        }

        public BankingAgentDbContext Context { get; }

        public static async Task<TestDatabase> CreateAsync()
        {
            var connection = new SqliteConnection("Data Source=:memory:");
            await connection.OpenAsync();
            var options = new DbContextOptionsBuilder<BankingAgentDbContext>()
                .UseSqlite(connection)
                .Options;
            var context = new BankingAgentDbContext(options);
            await context.Database.EnsureCreatedAsync();
            return new TestDatabase(connection, context);
        }

        public async Task<WorkflowState> SeedWorkflowAsync()
        {
            var now = DateTimeOffset.UtcNow;
            var workflow = new WorkflowState(
                Guid.NewGuid(),
                Guid.NewGuid().ToString("N"),
                "Dispute this charge.",
                WorkflowStatus.WaitingForApproval,
                "dispute",
                true,
                null,
                null,
                now,
                now,
                [new WorkflowEvent("workflow.started", "Workflow started", now, "system")],
                1);
            Context.Workflows.Add(new WorkflowEntity
            {
                Id = workflow.Id,
                TraceId = workflow.TraceId,
                UserMessage = workflow.UserMessage,
                Status = workflow.Status,
                Intent = workflow.Intent,
                RequiresApproval = workflow.RequiresApproval,
                ApprovalDecision = workflow.ApprovalDecision,
                ApprovalReason = workflow.ApprovalReason,
                CreatedAt = workflow.CreatedAt,
                UpdatedAt = workflow.UpdatedAt,
                Version = workflow.Version
            });
            Context.WorkflowEvents.Add(new WorkflowEventEntity
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                Sequence = 0,
                Type = workflow.Events[0].Type,
                Message = workflow.Events[0].Message,
                Timestamp = workflow.Events[0].Timestamp,
                Actor = workflow.Events[0].Actor,
                Details = workflow.Events[0].Details
            });
            await Context.SaveChangesAsync();
            Context.ChangeTracker.Clear();
            return workflow;
        }

        public async ValueTask DisposeAsync()
        {
            await Context.DisposeAsync();
            await _connection.DisposeAsync();
        }
    }
}
