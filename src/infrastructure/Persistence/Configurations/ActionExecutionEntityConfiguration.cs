using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class ActionExecutionEntityConfiguration
    : IEntityTypeConfiguration<ActionExecutionEntity>
{
    public void Configure(EntityTypeBuilder<ActionExecutionEntity> builder)
    {
        builder.ToTable("action_executions");
        builder.HasKey(action => action.Id);
        builder.Property(action => action.Id).HasColumnName("id");
        builder.Property(action => action.WorkflowId).HasColumnName("workflow_id");
        builder.Property(action => action.ActionType)
            .HasColumnName("action_type")
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(action => action.IdempotencyKey)
            .HasColumnName("idempotency_key")
            .HasMaxLength(128)
            .IsRequired();
        builder.HasIndex(action => action.IdempotencyKey).IsUnique();
        builder.Property(action => action.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(action => action.RequestedAt).HasColumnName("requested_at");
        builder.Property(action => action.CompletedAt).HasColumnName("completed_at");
        builder.Property(action => action.Result)
            .HasColumnName("result")
            .HasColumnType("jsonb");
        builder.Property(action => action.ErrorCode)
            .HasColumnName("error_code")
            .HasMaxLength(128);
    }
}
