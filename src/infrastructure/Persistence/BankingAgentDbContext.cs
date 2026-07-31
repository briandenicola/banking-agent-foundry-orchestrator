using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;

namespace BankingAgent.Infrastructure.Persistence;

public sealed class BankingAgentDbContext(DbContextOptions<BankingAgentDbContext> options)
    : DbContext(options)
{
    public DbSet<WorkflowEntity> Workflows => Set<WorkflowEntity>();
    public DbSet<WorkflowEventEntity> WorkflowEvents => Set<WorkflowEventEntity>();
    public DbSet<ApprovalDecisionEntity> ApprovalDecisions => Set<ApprovalDecisionEntity>();
    public DbSet<ActionExecutionEntity> ActionExecutions => Set<ActionExecutionEntity>();
    public DbSet<SupportCaseEntity> SupportCases => Set<SupportCaseEntity>();
    public DbSet<DemoTransactionEntity> DemoTransactions => Set<DemoTransactionEntity>();
    public DbSet<WorkflowEvidenceEntity> WorkflowEvidence => Set<WorkflowEvidenceEntity>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.ApplyConfigurationsFromAssembly(typeof(BankingAgentDbContext).Assembly);
    }
}
