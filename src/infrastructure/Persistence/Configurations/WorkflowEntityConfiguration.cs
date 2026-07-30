using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class WorkflowEntityConfiguration
    : IEntityTypeConfiguration<WorkflowEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEntity> builder)
    {
        builder.ToTable("workflows");
        builder.HasKey(workflow => workflow.Id);
        builder.Property(workflow => workflow.Id).HasColumnName("id");
        builder.Property(workflow => workflow.TraceId)
            .HasColumnName("trace_id")
            .HasMaxLength(64)
            .IsRequired();
        builder.HasIndex(workflow => workflow.TraceId).IsUnique();
        builder.Property(workflow => workflow.UserMessage)
            .HasColumnName("user_message")
            .IsRequired();
        builder.Property(workflow => workflow.Status)
            .HasColumnName("status")
            .HasConversion<string>()
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(workflow => workflow.Intent)
            .HasColumnName("intent")
            .HasMaxLength(128);
        builder.Property(workflow => workflow.RequiresApproval)
            .HasColumnName("requires_approval");
        builder.Property(workflow => workflow.ApprovalDecision)
            .HasColumnName("approval_decision")
            .HasMaxLength(32);
        builder.Property(workflow => workflow.ApprovalReason)
            .HasColumnName("approval_reason");
        builder.Property(workflow => workflow.CreatedAt).HasColumnName("created_at");
        builder.Property(workflow => workflow.UpdatedAt).HasColumnName("updated_at");
        builder.Property(workflow => workflow.Version)
            .HasColumnName("version")
            .IsConcurrencyToken();

        builder.HasMany(workflow => workflow.Events)
            .WithOne(workflowEvent => workflowEvent.Workflow)
            .HasForeignKey(workflowEvent => workflowEvent.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(workflow => workflow.Decisions)
            .WithOne(decision => decision.Workflow)
            .HasForeignKey(decision => decision.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasMany(workflow => workflow.ActionExecutions)
            .WithOne(action => action.Workflow)
            .HasForeignKey(action => action.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
        builder.HasOne(workflow => workflow.SupportCase)
            .WithOne(supportCase => supportCase.Workflow)
            .HasForeignKey<SupportCaseEntity>(supportCase => supportCase.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
