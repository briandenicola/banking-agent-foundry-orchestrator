using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

/// <summary>
/// Unit tests for WorkflowService behaviors that are independent of the
/// underlying persistence implementation.
///
/// Uses mock repositories so tests run without a database and focus on
/// service-level invariants: state transitions, approval idempotency,
/// and typed exception emission.
/// </summary>
public sealed class WorkflowServiceCurrentBehaviorTests
{
    private readonly Mock<IMcpClient> _mcpClient = new(MockBehavior.Strict);
    private readonly Mock<IWorkflowRepository> _repo = new(MockBehavior.Loose);
    private readonly Mock<IWorkflowActionRepository> _actionRepo = new(MockBehavior.Loose);
    private readonly WorkflowService _sut;

    public WorkflowServiceCurrentBehaviorTests()
    {
        _sut = new WorkflowService(
            _mcpClient.Object,
            NullLogger<WorkflowService>.Instance,
            _repo.Object,
            _actionRepo.Object);
    }

    [Fact]
    public async Task StartAsync_PlannerTransportFailure_PersistsFailedWorkflow()
    {
        WorkflowState? persistedDraft = null;
        WorkflowState? persistedFailure = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, CancellationToken>((workflow, _) => persistedDraft = workflow)
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.UpdateAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, long, CancellationToken>(
                (workflow, _, _) => persistedFailure = workflow)
            .Returns(Task.CompletedTask);
        _mcpClient.Setup(client => client.InvokeAsync(
                "workflow.plan",
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("Foundry unavailable"));

        var result = await _sut.StartAsync("Why is this charge pending?");

        Assert.NotNull(persistedDraft);
        Assert.Equal(WorkflowStatus.Draft, persistedDraft.Status);
        Assert.NotNull(persistedFailure);
        Assert.Equal(WorkflowStatus.Failed, persistedFailure.Status);
        Assert.Equal(WorkflowStatus.Failed, result.Status);
        Assert.Contains(result.Events, e => e.Type == "workflow.failed");
    }

    [Fact]
    public async Task StartAsync_CanceledPlanner_PersistsFailureBeforeRethrowing()
    {
        WorkflowState? persistedFailure = null;
        _repo.Setup(r => r.AddAsync(It.IsAny<WorkflowState>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        _repo.Setup(r => r.UpdateAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<long>(),
                CancellationToken.None))
            .Callback<WorkflowState, long, CancellationToken>(
                (workflow, _, _) => persistedFailure = workflow)
            .Returns(Task.CompletedTask);

        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _mcpClient.Setup(client => client.InvokeAsync(
                "workflow.plan",
                It.IsAny<IDictionary<string, object?>>(),
                cancellation.Token))
            .ThrowsAsync(new OperationCanceledException(cancellation.Token));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => _sut.StartAsync("Why is this charge pending?", cancellation.Token));

        Assert.NotNull(persistedFailure);
        Assert.Equal(WorkflowStatus.Failed, persistedFailure.Status);
    }

    // ──────────────────────────────────────────────────────────────────
    // ApproveAsync — not found
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_UnknownWorkflowId_ThrowsWorkflowNotFoundException()
    {
        var unknownId = Guid.NewGuid();
        _repo.Setup(r => r.GetAsync(unknownId, It.IsAny<CancellationToken>()))
             .ReturnsAsync((WorkflowState?)null);

        await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => _sut.ApproveAsync(unknownId, "approve", "reason"));
    }

    [Fact]
    public async Task ApproveAsync_UnknownWorkflowId_ExceptionCarriesWorkflowId()
    {
        var unknownId = Guid.NewGuid();
        _repo.Setup(r => r.GetAsync(unknownId, It.IsAny<CancellationToken>()))
             .ReturnsAsync((WorkflowState?)null);

        var ex = await Assert.ThrowsAsync<WorkflowNotFoundException>(
            () => _sut.ApproveAsync(unknownId, "approve", "reason"));

        Assert.Equal(unknownId, ex.WorkflowId);
    }

    // ──────────────────────────────────────────────────────────────────
    // ApproveAsync — invalid transition guard
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_WorkflowNotWaitingForApproval_ThrowsInvalidTransitionException()
    {
        var workflow = BuildWorkflow(WorkflowStatus.Completed);
        _repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow);

        await Assert.ThrowsAsync<InvalidTransitionException>(
            () => _sut.ApproveAsync(workflow.Id, "approve", "reason"));
    }

    [Theory]
    [InlineData(WorkflowStatus.Draft)]
    [InlineData(WorkflowStatus.Failed)]
    [InlineData(WorkflowStatus.Rejected)]
    public async Task ApproveAsync_NonWaitingStatuses_ThrowsInvalidTransitionException(WorkflowStatus status)
    {
        var workflow = BuildWorkflow(status);
        _repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow);

        await Assert.ThrowsAsync<InvalidTransitionException>(
            () => _sut.ApproveAsync(workflow.Id, "approve", "reason"));
    }

    // ──────────────────────────────────────────────────────────────────
    // ApproveAsync — idempotency (same decision)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_SameDecisionAlreadyRecorded_ReturnsCurrent_Idempotent()
    {
        // Workflow already approved — same decision should not error.
        var workflow = BuildWorkflow(WorkflowStatus.Completed, approvalDecision: "approve");
        _repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow);

        var result = await _sut.ApproveAsync(workflow.Id, "approve", "duplicate call");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal("approve", result.ApprovalDecision);
        // Repository must NOT be called for a new write
        _actionRepo.Verify(
            r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(), It.IsAny<ApprovalDecision>(),
                It.IsAny<ActionExecution?>(), It.IsAny<SupportCase?>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()),
            Times.Never);
    }

    // ──────────────────────────────────────────────────────────────────
    // ApproveAsync — conflict (different decision)
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_ConflictingDecision_ThrowsConflictingDecisionException()
    {
        var workflow = BuildWorkflow(WorkflowStatus.Completed, approvalDecision: "approve");
        _repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow);

        await Assert.ThrowsAsync<ConflictingDecisionException>(
            () => _sut.ApproveAsync(workflow.Id, "reject", "second opinion"));
    }

    [Fact]
    public async Task ApproveAsync_ConflictingDecision_ExceptionCarriesDecisionDetails()
    {
        var workflow = BuildWorkflow(WorkflowStatus.Completed, approvalDecision: "approve");
        _repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow);

        var ex = await Assert.ThrowsAsync<ConflictingDecisionException>(
            () => _sut.ApproveAsync(workflow.Id, "reject", "second opinion"));

        Assert.Equal("approve", ex.ExistingDecision);
        Assert.Equal("reject", ex.NewDecision);
        Assert.Equal(workflow.Id, ex.WorkflowId);
    }

    // ──────────────────────────────────────────────────────────────────
    // ApproveAsync — happy path
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ApproveAsync_WaitingWorkflow_ApproveDecision_SetsCompletedStatus()
    {
        var workflow = BuildWorkflow(WorkflowStatus.WaitingForApproval);
        _repo.SetupSequence(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow)
             .ReturnsAsync(workflow with
             {
                 Status = WorkflowStatus.Completed,
                 ApprovalDecision = "approve",
                 ApprovalReason = "all clear",
                 Version = workflow.Version + 1
             });
        _actionRepo.Setup(r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(), It.IsAny<ApprovalDecision>(),
                It.IsAny<ActionExecution>(), It.IsAny<SupportCase>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.ApproveAsync(workflow.Id, "approve", "all clear");

        Assert.Equal(WorkflowStatus.Completed, result.Status);
        Assert.Equal("approve", result.ApprovalDecision);
        Assert.Equal("all clear", result.ApprovalReason);
    }

    [Fact]
    public async Task ApproveAsync_WaitingWorkflow_RejectDecision_SetsRejectedStatus()
    {
        var workflow = BuildWorkflow(WorkflowStatus.WaitingForApproval);
        _repo.SetupSequence(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow)
             .ReturnsAsync(workflow with
             {
                 Status = WorkflowStatus.Rejected,
                 ApprovalDecision = "reject",
                 Version = workflow.Version + 1
             });
        _actionRepo.Setup(r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(), It.IsAny<ApprovalDecision>(),
                null, null, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.ApproveAsync(workflow.Id, "reject", "compliance block");

        Assert.Equal(WorkflowStatus.Rejected, result.Status);
    }

    [Fact]
    public async Task ApproveAsync_ApprovedDispute_CreatesCompletedActionAndSupportCase()
    {
        var workflow = BuildWorkflow(WorkflowStatus.WaitingForApproval);
        _repo.SetupSequence(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow)
             .ReturnsAsync((WorkflowState?)null);
        _actionRepo.Setup(r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(), It.IsAny<ApprovalDecision>(),
                It.IsAny<ActionExecution>(), It.IsAny<SupportCase>(),
                It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.ApproveAsync(workflow.Id, "approve", "smoke approval");

        Assert.Contains(result.Events, e => e.Type == "workflow.approval");
        Assert.Contains(result.Events, e => e.Type == "workflow.action_completed");
        _actionRepo.Verify(r => r.RecordDecisionAsync(
            It.Is<WorkflowState>(state => state.Status == WorkflowStatus.Completed),
            It.Is<ApprovalDecision>(approval => approval.Decision == "approve"),
            It.Is<ActionExecution>(action =>
                action.ActionType == "dispute.support_case.create" &&
                action.Status == ActionExecutionStatus.Completed &&
                action.IdempotencyKey == $"dispute-support-case:{workflow.Id:N}"),
            It.Is<SupportCase>(supportCase =>
                supportCase.WorkflowId == workflow.Id &&
                supportCase.CaseNumber == $"DSP-{workflow.Id:N}" &&
                supportCase.Status == "Open"),
            workflow.Version,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApproveAsync_RejectedDispute_CreatesNoActionOrSupportCase()
    {
        var workflow = BuildWorkflow(WorkflowStatus.WaitingForApproval);
        _repo.SetupSequence(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow)
             .ReturnsAsync((WorkflowState?)null);
        _actionRepo.Setup(r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(), It.IsAny<ApprovalDecision>(),
                null, null, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        var result = await _sut.ApproveAsync(workflow.Id, "reject", "insufficient evidence");

        Assert.Equal(WorkflowStatus.Rejected, result.Status);
        Assert.DoesNotContain(result.Events, e => e.Type == "workflow.action_completed");
        _actionRepo.Verify(r => r.RecordDecisionAsync(
            It.IsAny<WorkflowState>(),
            It.IsAny<ApprovalDecision>(),
            null,
            null,
            workflow.Version,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    [Fact]
    public async Task ApproveAsync_ApprovedSuspiciousAction_CreatesNoSupportCase()
    {
        var workflow = BuildWorkflow(
            WorkflowStatus.WaitingForApproval,
            userMessage: "Freeze my card because this charge looks fraudulent.");
        _repo.SetupSequence(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow)
             .ReturnsAsync((WorkflowState?)null);
        _actionRepo.Setup(r => r.RecordDecisionAsync(
                It.IsAny<WorkflowState>(), It.IsAny<ApprovalDecision>(),
                null, null, It.IsAny<long>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);

        await _sut.ApproveAsync(workflow.Id, "approve", "Customer confirmed");

        _actionRepo.Verify(r => r.RecordDecisionAsync(
            It.IsAny<WorkflowState>(),
            It.IsAny<ApprovalDecision>(),
            null,
            null,
            workflow.Version,
            It.IsAny<CancellationToken>()),
            Times.Once);
    }

    // ──────────────────────────────────────────────────────────────────
    // GetAsync — delegates to repository
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task GetAsync_ExistingId_DelegatesToRepository()
    {
        var workflow = BuildWorkflow(WorkflowStatus.Completed);
        _repo.Setup(r => r.GetAsync(workflow.Id, It.IsAny<CancellationToken>()))
             .ReturnsAsync(workflow);

        var result = await _sut.GetAsync(workflow.Id);

        Assert.NotNull(result);
        Assert.Equal(workflow.Id, result.Id);
    }

    [Fact]
    public async Task GetAsync_MissingId_ReturnsNull()
    {
        var missingId = Guid.NewGuid();
        _repo.Setup(r => r.GetAsync(missingId, It.IsAny<CancellationToken>()))
             .ReturnsAsync((WorkflowState?)null);

        var result = await _sut.GetAsync(missingId);

        Assert.Null(result);
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static WorkflowState BuildWorkflow(
        WorkflowStatus status,
        string? approvalDecision = null,
        long version = 1,
        string userMessage = "Dispute this charge.") =>
        new(
            Id: Guid.NewGuid(),
            TraceId: Guid.NewGuid().ToString("N"),
            UserMessage: userMessage,
            Status: status,
            Intent: "dispute",
            RequiresApproval: true,
            ApprovalDecision: approvalDecision,
            ApprovalReason: null,
            CreatedAt: DateTimeOffset.UtcNow,
            UpdatedAt: DateTimeOffset.UtcNow,
            Events: [new WorkflowEvent("workflow.started", "Workflow started", DateTimeOffset.UtcNow, "system")],
            Version: version);
}
