using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class ApprovalDecisionEntityConfiguration
    : IEntityTypeConfiguration<ApprovalDecisionEntity>
{
    public void Configure(EntityTypeBuilder<ApprovalDecisionEntity> builder)
    {
        builder.ToTable("approval_decisions");
        builder.HasKey(decision => decision.Id);
        builder.Property(decision => decision.Id).HasColumnName("id");
        builder.Property(decision => decision.WorkflowId).HasColumnName("workflow_id");
        builder.HasIndex(decision => decision.WorkflowId).IsUnique();
        builder.Property(decision => decision.Decision)
            .HasColumnName("decision")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(decision => decision.Reason)
            .HasColumnName("reason")
            .IsRequired();
        builder.Property(decision => decision.Actor)
            .HasColumnName("actor")
            .HasMaxLength(256)
            .IsRequired();
        builder.Property(decision => decision.CreatedAt).HasColumnName("created_at");
    }
}
