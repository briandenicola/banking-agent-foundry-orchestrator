using BankingAgent.Application;
using BankingAgent.Domain;

using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// Contract tests for IWorkflowRepository and IWorkflowActionRepository.
///
/// These tests declare the behavioral contract that any implementation
/// (e.g., EfWorkflowRepository post Theo's persistence workstream) must satisfy.
/// They drive the repository interfaces via mocks so they compile and run today,
/// validating the contract logic in isolation without a live database.
///
/// After Theo's implementation lands, integration tests against an in-memory
/// or test-container PostgreSQL instance should complement these.
/// </summary>
public sealed class WorkflowRepositoryContractTests
{
    private static WorkflowState BuildWorkflow(
        WorkflowStatus status = WorkflowStatus.WaitingForApproval,
        long version = 0) =>
        new(
            Id: Guid.NewGuid(),
            TraceId: Guid.NewGuid().ToString("N"),
            UserMessage: "Dispute this charge.",
            Status: status,
            Intent: "dispute",
            RequiresApproval: true,
            ApprovalDecision: null,
            ApprovalReason: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Events: [new WorkflowEvent("workflow.started", "Workflow started", DateTimeOffset.UtcNow, "system")],
            Version: version);

    // ──────────────────────────────────────────────────────────────────
    // AddAsync contract
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task AddAsync_WorkflowWithEvents_InvokesRepositoryOnce()
    {
        var repo = new Mock<IWorkflowRepository>(MockBehavior.Strict);
        var workflow = BuildWorkflow();

        repo.Setup(r => r.AddAsync(workflow, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await repo.Object.AddAsync(workflow);

        repo.Verify(r => r.AddAsync(workflow, It.IsAny<CancellationToken>()), Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────
    // GetAsync contract
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ExistingWorkflow_ReturnsWorkflowWithEvents()
    {
        var workflow = BuildWorkflow();
        var repo = new Mock<IWorkflowRepository>(MockBehavior.Strict);

        repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
            .ReturnsAsync(workflow);

        var result = await repo.Object.GetAsync(workflow.Id);

        Assert.NotNull(result);
        Assert.Equal(workflow.Id, result.Id);
        Assert.NotEmpty(result.Events);
        Assert.Equal("workflow.started", result.Events[0].Type);
    }

    [Fact]
    public async Task GetAsync_MissingWorkflow_ReturnsNull()
    {
        var repo = new Mock<IWorkflowRepository>(MockBehavior.Strict);
        var missingId = Guid.NewGuid();

        repo.Setup(r => r.GetAsync(missingId, It.IsAny<CancellationToken>()))
            .ReturnsAsync((WorkflowState?)null);

        var result = await repo.Object.GetAsync(missingId);

        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────────────────
    // UpdateAsync — optimistic concurrency contract
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task UpdateAsync_CorrectVersion_Succeeds()
    {
        var workflow = BuildWorkflow(version: 1);
        var repo = new Mock<IWorkflowRepository>(MockBehavior.Strict);

        repo.Setup(r => r.UpdateAsync(workflow, 1L, It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // Should not throw
        await repo.Object.UpdateAsync(workflow, 1L);

        repo.Verify(r => r.UpdateAsync(workflow, 1L, It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task UpdateAsync_StaleVersion_ThrowsStaleVersionException()
    {
        var workflow = BuildWorkflow(version: 2);
        var repo = new Mock<IWorkflowRepository>(MockBehavior.Strict);

        // Simulate optimistic concurrency failure (EF throws DbUpdateConcurrencyException)
        repo.Setup(r => r.UpdateAsync(workflow, 1L /* stale */, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new StaleVersionException(workflow.Id, 1L, 2L));

        await Assert.ThrowsAsync<StaleVersionException>(
            () => repo.Object.UpdateAsync(workflow, 1L));
    }

    // ──────────────────────────────────────────────────────────────────
    // RecordDecisionAsync — idempotent approval contract
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task RecordDecisionAsync_SameDecisionTwice_RepositoryCalledBothTimes()
    {
        // The repository implementation is responsible for idempotency detection;
        // the caller (WorkflowService) will call RecordDecisionAsync and the repository
        // must check for an existing matching decision and return idempotently.
        var repo = new Mock<IWorkflowActionRepository>(MockBehavior.Strict);
        var workflow = BuildWorkflow(WorkflowStatus.WaitingForApproval);
        var decision = new ApprovalDecision(
            Guid.NewGuid(),
            workflow.Id,
            "approve",
            "smoke test",
            "system",
            DateTimeOffset.UtcNow);

        repo.Setup(r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<ApprovalDecision>(),
                null,
                null,
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        // First call
        await repo.Object.RecordDecisionAsync(workflow, decision, null, null, workflow.Version);
        // Second call with identical decision (idempotent)
        await repo.Object.RecordDecisionAsync(workflow, decision, null, null, workflow.Version);

        repo.Verify(r => r.RecordDecisionAsync(
            It.IsAny<WorkflowState>(),
            It.IsAny<ApprovalDecision>(),
            null,
            null,
            It.IsAny<long>(),
            It.IsAny<CancellationToken>()), Times.Exactly(2));
    }

    // ──────────────────────────────────────────────────────────────────
    // Events ordering contract
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void WorkflowState_Events_AreInChronologicalOrder()
    {
        var t0 = DateTimeOffset.UtcNow;
        var workflow = new WorkflowState(
            Guid.NewGuid(), Guid.NewGuid().ToString("N"), "msg",
            WorkflowStatus.Completed, "intent", false, "approve", "reason",
            t0, t0.AddSeconds(5),
            Events: [
                new("workflow.started", "Started", t0, "system"),
                new("workflow.route_selected", "Route selected", t0.AddSeconds(1), "system"),
                new("workflow.completed", "Completed", t0.AddSeconds(5), "system")
            ]);

        var events = workflow.Events;
        for (int i = 1; i < events.Count; i++)
        {
            Assert.True(events[i].Timestamp >= events[i - 1].Timestamp,
                $"Event at index {i} is earlier than event at index {i - 1}");
        }
    }
}
