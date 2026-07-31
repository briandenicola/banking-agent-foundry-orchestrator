using BankingAgent.Application;
using BankingAgent.Domain;
using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BankingAgent.Infrastructure.Persistence;

public sealed class EfWorkflowEvidenceRepository(BankingAgentDbContext context)
    : IWorkflowEvidenceRepository
{
    public async Task<IReadOnlyList<WorkflowEvidence>> ListAsync(
        Guid workflowId,
        CancellationToken cancellationToken = default)
    {
        var query = context.WorkflowEvidence
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId)
            .Select(item => new
            {
                item.Id,
                item.WorkflowId,
                item.FileName,
                item.ContentType,
                item.Length,
                item.Sha256,
                item.UploadedAt
            });
        var metadata = string.Equals(
            context.Database.ProviderName,
            "Microsoft.EntityFrameworkCore.Sqlite",
            StringComparison.Ordinal)
            ? (await query.ToListAsync(cancellationToken))
                .OrderBy(item => item.UploadedAt)
                .ToList()
            : await query
                .OrderBy(item => item.UploadedAt)
                .ToListAsync(cancellationToken);

        return metadata
            .Select(item => new WorkflowEvidence(
                item.Id,
                item.WorkflowId,
                item.FileName,
                item.ContentType,
                item.Length,
                item.Sha256,
                [],
                item.UploadedAt))
            .ToList();
    }

    public async Task<WorkflowEvidence?> GetAsync(
        Guid workflowId,
        Guid evidenceId,
        CancellationToken cancellationToken = default) =>
        await context.WorkflowEvidence
            .AsNoTracking()
            .Where(item => item.WorkflowId == workflowId && item.Id == evidenceId)
            .Select(item => new WorkflowEvidence(
                item.Id,
                item.WorkflowId,
                item.FileName,
                item.ContentType,
                item.Length,
                item.Sha256,
                item.Content,
                item.UploadedAt))
            .SingleOrDefaultAsync(cancellationToken);

    public async Task AddAsync(
        IReadOnlyList<WorkflowEvidence> evidence,
        CancellationToken cancellationToken = default)
    {
        context.WorkflowEvidence.AddRange(evidence.Select(item => new WorkflowEvidenceEntity
        {
            Id = item.Id,
            WorkflowId = item.WorkflowId,
            FileName = item.FileName,
            ContentType = item.ContentType,
            Length = item.Length,
            Sha256 = item.Sha256,
            Content = item.Content,
            UploadedAt = item.UploadedAt
        }));
        await context.SaveChangesAsync(cancellationToken);
        context.ChangeTracker.Clear();
    }
}
