using BankingAgent.Application;
using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BankingAgent.Infrastructure.Persistence;

public sealed class EfWorkflowActionRepository(BankingAgentDbContext context) : IWorkflowActionRepository
{
    public async Task<SupportCase?> GetSupportCaseAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.SupportCases
            .FirstOrDefaultAsync(sc => sc.WorkflowId == workflowId, cancellationToken);

        return entity is null ? null : MapSupportCase(entity);
    }

    public async Task RecordDecisionAsync(
        WorkflowState workflow,
        ApprovalDecision decision,
        ActionExecution? actionExecution,
        SupportCase? supportCase,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var existing = await context.ApprovalDecisions
            .AsNoTracking()
            .SingleOrDefaultAsync(item => item.WorkflowId == workflow.Id, cancellationToken);
        if (existing is not null)
        {
            if (string.Equals(existing.Decision, decision.Decision, StringComparison.OrdinalIgnoreCase))
                return;

            throw new ConflictingDecisionException(workflow.Id, existing.Decision, decision.Decision);
        }

        var currentVersion = await context.Workflows
            .AsNoTracking()
            .Where(item => item.Id == workflow.Id)
            .Select(item => (long?)item.Version)
            .SingleOrDefaultAsync(cancellationToken)
            ?? throw new WorkflowNotFoundException(workflow.Id);

        if (currentVersion != expectedVersion)
            throw new StaleVersionException(workflow.Id, expectedVersion, currentVersion);

        var existingEventCount = await context.WorkflowEvents
            .AsNoTracking()
            .CountAsync(item => item.WorkflowId == workflow.Id, cancellationToken);

        await using var transaction = await context.Database.BeginTransactionAsync(cancellationToken);
        var affectedRows = await context.Workflows
            .Where(item => item.Id == workflow.Id && item.Version == expectedVersion)
            .ExecuteUpdateAsync(
                setters => setters
                    .SetProperty(item => item.Status, workflow.Status)
                    .SetProperty(item => item.ApprovalDecision, workflow.ApprovalDecision)
                    .SetProperty(item => item.ApprovalReason, workflow.ApprovalReason)
                    .SetProperty(item => item.UpdatedAt, workflow.UpdatedAt)
                    .SetProperty(item => item.Version, workflow.Version),
                cancellationToken);

        if (affectedRows != 1)
        {
            throw new StaleVersionException(workflow.Id, expectedVersion, -1);
        }

        AddNewEvents(workflow, existingEventCount);

        context.ApprovalDecisions.Add(new ApprovalDecisionEntity
        {
            Id = decision.Id,
            WorkflowId = decision.WorkflowId,
            Decision = decision.Decision,
            Reason = decision.Reason,
            Actor = decision.Actor,
            CreatedAt = decision.CreatedAt
        });

        if (actionExecution is not null)
        {
            context.ActionExecutions.Add(new ActionExecutionEntity
            {
                Id = actionExecution.Id,
                WorkflowId = actionExecution.WorkflowId,
                ActionType = actionExecution.ActionType,
                IdempotencyKey = actionExecution.IdempotencyKey,
                Status = actionExecution.Status,
                RequestedAt = actionExecution.RequestedAt,
                CompletedAt = actionExecution.CompletedAt,
                Result = actionExecution.Result,
                ErrorCode = actionExecution.ErrorCode
            });
        }

        if (supportCase is not null)
        {
            context.SupportCases.Add(new SupportCaseEntity
            {
                Id = supportCase.Id,
                WorkflowId = supportCase.WorkflowId,
                CaseNumber = supportCase.CaseNumber,
                Status = supportCase.Status,
                Summary = supportCase.Summary,
                CreatedAt = supportCase.CreatedAt,
                UpdatedAt = supportCase.UpdatedAt
            });
        }

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }
        catch (DbUpdateException)
        {
            await transaction.RollbackAsync(CancellationToken.None);
            if (await DecisionWasRecordedAsync(decision, cancellationToken))
            {
                return;
            }

            throw;
        }
    }

    private async Task<bool> DecisionWasRecordedAsync(
        ApprovalDecision decision,
        CancellationToken cancellationToken)
    {
        context.ChangeTracker.Clear();
        var existing = await context.ApprovalDecisions
            .AsNoTracking()
            .SingleOrDefaultAsync(
                item => item.WorkflowId == decision.WorkflowId,
                cancellationToken);

        if (existing is null)
        {
            return false;
        }

        if (string.Equals(
                existing.Decision,
                decision.Decision,
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        throw new ConflictingDecisionException(
            decision.WorkflowId,
            existing.Decision,
            decision.Decision);
    }

    private void AddNewEvents(WorkflowState workflow, int existingCount)
    {
        for (var i = existingCount; i < workflow.Events.Count; i++)
        {
            var e = workflow.Events[i];
            context.WorkflowEvents.Add(new WorkflowEventEntity
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                Sequence = i,
                Type = e.Type,
                Message = e.Message,
                Timestamp = e.Timestamp,
                Actor = e.Actor,
                Details = e.Details
            });
        }
    }

    private static SupportCase MapSupportCase(SupportCaseEntity entity) =>
        new(entity.Id, entity.WorkflowId, entity.CaseNumber,
            entity.Status, entity.Summary, entity.CreatedAt, entity.UpdatedAt);
}
