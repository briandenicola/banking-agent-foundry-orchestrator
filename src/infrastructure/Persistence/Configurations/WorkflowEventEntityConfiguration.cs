using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class WorkflowEventEntityConfiguration
    : IEntityTypeConfiguration<WorkflowEventEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEventEntity> builder)
    {
        builder.ToTable("workflow_events");
        builder.HasKey(workflowEvent => workflowEvent.Id);
        builder.Property(workflowEvent => workflowEvent.Id).HasColumnName("id");
        builder.Property(workflowEvent => workflowEvent.WorkflowId).HasColumnName("workflow_id");
        builder.Property(workflowEvent => workflowEvent.Sequence).HasColumnName("sequence");
        builder.HasIndex(workflowEvent => new { workflowEvent.WorkflowId, workflowEvent.Sequence })
            .IsUnique();
        builder.Property(workflowEvent => workflowEvent.Type)
            .HasColumnName("type")
            .HasMaxLength(128)
            .IsRequired();
        builder.Property(workflowEvent => workflowEvent.Message)
            .HasColumnName("message")
            .HasMaxLength(512)
            .IsRequired();
        builder.Property(workflowEvent => workflowEvent.Timestamp).HasColumnName("timestamp");
        builder.Property(workflowEvent => workflowEvent.Actor)
            .HasColumnName("actor")
            .HasMaxLength(256);
        builder.Property(workflowEvent => workflowEvent.Details).HasColumnName("details");
    }
}
