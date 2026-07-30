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
        var entity = await context.Workflows
            .Include(w => w.Events)
            .Include(w => w.Decisions)
            .FirstOrDefaultAsync(w => w.Id == workflow.Id, cancellationToken)
            ?? throw new WorkflowNotFoundException(workflow.Id);

        // Idempotency and conflict check before touching any state.
        var existing = entity.Decisions.SingleOrDefault();
        if (existing is not null)
        {
            if (string.Equals(existing.Decision, decision.Decision, StringComparison.OrdinalIgnoreCase))
                return; // Identical decision already recorded — idempotent success.

            throw new ConflictingDecisionException(workflow.Id, existing.Decision, decision.Decision);
        }

        if (entity.Version != expectedVersion)
            throw new StaleVersionException(workflow.Id, expectedVersion, entity.Version);

        // Apply updated workflow scalar fields.
        entity.Status = workflow.Status;
        entity.ApprovalDecision = workflow.ApprovalDecision;
        entity.ApprovalReason = workflow.ApprovalReason;
        entity.UpdatedAt = workflow.UpdatedAt;

        // Append new events (the approval event added by the service layer).
        AppendNewEvents(entity, workflow);

        // Record the approval decision detail.
        entity.Decisions.Add(new ApprovalDecisionEntity
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
            entity.ActionExecutions.Add(new ActionExecutionEntity
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
            entity.SupportCase = new SupportCaseEntity
            {
                Id = supportCase.Id,
                WorkflowId = supportCase.WorkflowId,
                CaseNumber = supportCase.CaseNumber,
                Status = supportCase.Status,
                Summary = supportCase.Summary,
                CreatedAt = supportCase.CreatedAt,
                UpdatedAt = supportCase.UpdatedAt
            };
        }

        // Increment version; EF uses the original tracked value for WHERE version=@expected.
        entity.Version = workflow.Version;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
        }
        catch (DbUpdateConcurrencyException)
        {
            if (await DecisionWasRecordedAsync(decision, cancellationToken))
            {
                return;
            }

            throw new StaleVersionException(workflow.Id, expectedVersion, -1);
        }
        catch (DbUpdateException)
        {
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

    private static void AppendNewEvents(WorkflowEntity entity, WorkflowState workflow)
    {
        var existingCount = entity.Events.Count;
        for (var i = existingCount; i < workflow.Events.Count; i++)
        {
            var e = workflow.Events[i];
            entity.Events.Add(new WorkflowEventEntity
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
