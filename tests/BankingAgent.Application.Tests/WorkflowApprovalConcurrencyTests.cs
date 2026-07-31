using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace BankingAgent.Application.Tests;

public sealed class WorkflowApprovalConcurrencyTests
{
    [Fact]
    public async Task ConcurrentMatchingApprovals_CreateOneActionAndReturnSameOutcome()
    {
        var workflow = BuildWorkflow();
        var store = new CoordinatedWorkflowStore(workflow);
        var firstService = CreateService(store);
        var secondService = CreateService(store);

        var results = await Task.WhenAll(
            firstService.ApproveAsync(workflow.Id, "approve", "First delivery"),
            secondService.ApproveAsync(workflow.Id, "approve", "Retry delivery"));

        Assert.All(results, result => Assert.Equal(WorkflowStatus.Completed, result.Status));
        Assert.All(results, result => Assert.Equal("approve", result.ApprovalDecision));
        Assert.Equal(1, store.DecisionCount);
        Assert.Equal(1, store.ActionCount);
        Assert.Equal(1, store.SupportCaseCount);
    }

    [Fact]
    public async Task ConcurrentConflictingApprovals_ProduceOneWinnerAndOneConflict()
    {
        var workflow = BuildWorkflow();
        var store = new CoordinatedWorkflowStore(workflow);
        var firstService = CreateService(store);
        var secondService = CreateService(store);

        var attempts = await Task.WhenAll(
            CaptureAsync(() => firstService.ApproveAsync(
                workflow.Id,
                "approve",
                "Approve delivery")),
            CaptureAsync(() => secondService.ApproveAsync(
                workflow.Id,
                "reject",
                "Reject delivery")));

        Assert.Single(attempts, attempt => attempt.Result is not null);
        Assert.Single(
            attempts,
            attempt => attempt.Exception is ConflictingDecisionException);
        Assert.Equal(1, store.DecisionCount);
        Assert.InRange(store.ActionCount, 0, 1);
        Assert.Equal(store.ActionCount, store.SupportCaseCount);
    }

    private static async Task<ApprovalAttempt> CaptureAsync(
        Func<Task<WorkflowState>> operation)
    {
        try
        {
            return new ApprovalAttempt(await operation(), null);
        }
        catch (Exception exception)
        {
            return new ApprovalAttempt(null, exception);
        }
    }

    private static WorkflowService CreateService(CoordinatedWorkflowStore store) =>
        new(
            new UnusedMcpClient(),
            NullLogger<WorkflowService>.Instance,
            store,
            store);

    private static WorkflowState BuildWorkflow()
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

    private sealed record ApprovalAttempt(
        WorkflowState? Result,
        Exception? Exception);

    private sealed class CoordinatedWorkflowStore(WorkflowState initialWorkflow)
        : IWorkflowRepository, IWorkflowActionRepository
    {
        private readonly object _gate = new();
        private readonly TaskCompletionSource _initialReadsCompleted =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        private WorkflowState _workflow = initialWorkflow;
        private ApprovalDecision? _decision;
        private ActionExecution? _action;
        private SupportCase? _supportCase;
        private int _initialReads;

        public int DecisionCount => _decision is null ? 0 : 1;
        public int ActionCount => _action is null ? 0 : 1;
        public int SupportCaseCount => _supportCase is null ? 0 : 1;

        public Task AddAsync(
            WorkflowState workflow,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("The concurrency workflow is preloaded.");

        public async Task<WorkflowState?> GetAsync(
            Guid workflowId,
            CancellationToken cancellationToken = default)
        {
            var read = Interlocked.Increment(ref _initialReads);
            if (read <= 2)
            {
                if (read == 2)
                {
                    _initialReadsCompleted.TrySetResult();
                }

                await _initialReadsCompleted.Task.WaitAsync(cancellationToken);
            }

            lock (_gate)
            {
                return _workflow;
            }
        }

        public Task UpdateAsync(
            WorkflowState workflow,
            long expectedVersion,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Approval writes use RecordDecisionAsync.");

        public Task<SupportCase?> GetSupportCaseAsync(
            Guid workflowId,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                return Task.FromResult(_supportCase);
            }
        }

        public Task RecordDecisionAsync(
            WorkflowState workflow,
            ApprovalDecision decision,
            ActionExecution? actionExecution,
            SupportCase? supportCase,
            long expectedVersion,
            CancellationToken cancellationToken = default)
        {
            lock (_gate)
            {
                if (_decision is not null)
                {
                    if (string.Equals(
                            _decision.Decision,
                            decision.Decision,
                            StringComparison.OrdinalIgnoreCase))
                    {
                        return Task.CompletedTask;
                    }

                    throw new ConflictingDecisionException(
                        workflow.Id,
                        _decision.Decision,
                        decision.Decision);
                }

                Assert.Equal(expectedVersion, _workflow.Version);
                _workflow = workflow;
                _decision = decision;
                _action = actionExecution;
                _supportCase = supportCase;
                return Task.CompletedTask;
            }
        }
    }

    private sealed class UnusedMcpClient : IMcpClient
    {
        public Task<McpToolResult> InvokeAsync(
            string toolName,
            IDictionary<string, object?> parameters,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Agent invocation is not used during approval.");
    }
}
