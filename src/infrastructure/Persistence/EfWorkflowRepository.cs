using BankingAgent.Application;
using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BankingAgent.Infrastructure.Persistence;

public sealed class EfWorkflowRepository(BankingAgentDbContext context) : IWorkflowRepository
{
    public async Task AddAsync(WorkflowState workflow, CancellationToken cancellationToken = default)
    {
        var entity = MapToEntity(workflow);
        context.Workflows.Add(entity);
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    public async Task<WorkflowState?> GetAsync(Guid workflowId, CancellationToken cancellationToken = default)
    {
        var entity = await context.Workflows
            .Include(w => w.Events)
            .FirstOrDefaultAsync(w => w.Id == workflowId, cancellationToken);

        return entity is null ? null : MapToDomain(entity);
    }

    public async Task UpdateAsync(
        WorkflowState workflow,
        long expectedVersion,
        CancellationToken cancellationToken = default)
    {
        var entity = await context.Workflows
            .Include(w => w.Events)
            .FirstOrDefaultAsync(w => w.Id == workflow.Id, cancellationToken)
            ?? throw new WorkflowNotFoundException(workflow.Id);

        if (entity.Version != expectedVersion)
            throw new StaleVersionException(workflow.Id, expectedVersion, entity.Version);

        entity.Status = workflow.Status;
        entity.Intent = workflow.Intent;
        entity.RequiresApproval = workflow.RequiresApproval;
        entity.ApprovalDecision = workflow.ApprovalDecision;
        entity.ApprovalReason = workflow.ApprovalReason;
        entity.UpdatedAt = workflow.UpdatedAt;

        AppendNewEvents(entity, workflow);

        context.Entry(entity).Property(item => item.Version).OriginalValue = expectedVersion;
        entity.Version = workflow.Version;

        try
        {
            await context.SaveChangesAsync(cancellationToken);
            context.ChangeTracker.Clear();
        }
        catch (DbUpdateConcurrencyException)
        {
            throw new StaleVersionException(workflow.Id, expectedVersion, -1);
        }
    }

    // -------------------------------------------------------------------------
    // Mapping — domain ↔ entity. No AutoMapper; explicit and testable.
    // -------------------------------------------------------------------------

    internal static WorkflowEntity MapToEntity(WorkflowState workflow)
    {
        var entity = new WorkflowEntity
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
        };

        for (var i = 0; i < workflow.Events.Count; i++)
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

        return entity;
    }

    internal static WorkflowState MapToDomain(WorkflowEntity entity)
    {
        var events = entity.Events
            .OrderBy(e => e.Sequence)
            .Select(e => new WorkflowEvent(e.Type, e.Message, e.Timestamp, e.Actor, e.Details))
            .ToList();

        return new WorkflowState(
            Id: entity.Id,
            TraceId: entity.TraceId,
            UserMessage: entity.UserMessage,
            Status: entity.Status,
            Intent: entity.Intent,
            RequiresApproval: entity.RequiresApproval,
            ApprovalDecision: entity.ApprovalDecision,
            ApprovalReason: entity.ApprovalReason,
            CreatedAt: entity.CreatedAt,
            UpdatedAt: entity.UpdatedAt,
            Events: events,
            Version: entity.Version);
    }

    // Appends events present in the domain state but not yet persisted.
    // Uses position (entity event count) rather than identity comparison so
    // the mapping stays O(n) with no allocation beyond the new entities.
    private void AppendNewEvents(WorkflowEntity entity, WorkflowState workflow)
    {
        var existingCount = entity.Events.Count;
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
}
