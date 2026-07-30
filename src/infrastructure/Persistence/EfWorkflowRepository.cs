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
                    .SetProperty(item => item.Intent, workflow.Intent)
                    .SetProperty(item => item.RequiresApproval, workflow.RequiresApproval)
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
        await context.SaveChangesAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }

    private void AddNewEvents(WorkflowState workflow, int existingCount)
    {
        for (var i = existingCount; i < workflow.Events.Count; i++)
        {
            var item = workflow.Events[i];
            context.WorkflowEvents.Add(new WorkflowEventEntity
            {
                Id = Guid.NewGuid(),
                WorkflowId = workflow.Id,
                Sequence = i,
                Type = item.Type,
                Message = item.Message,
                Timestamp = item.Timestamp,
                Actor = item.Actor,
                Details = item.Details
            });
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

}
