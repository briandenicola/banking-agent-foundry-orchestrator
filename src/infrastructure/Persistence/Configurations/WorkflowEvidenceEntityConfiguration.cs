using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class WorkflowEvidenceEntityConfiguration
    : IEntityTypeConfiguration<WorkflowEvidenceEntity>
{
    public void Configure(EntityTypeBuilder<WorkflowEvidenceEntity> builder)
    {
        builder.ToTable("workflow_evidence");
        builder.HasKey(item => item.Id);
        builder.Property(item => item.Id).HasColumnName("id");
        builder.Property(item => item.WorkflowId).HasColumnName("workflow_id");
        builder.Property(item => item.FileName)
            .HasColumnName("file_name")
            .HasMaxLength(255)
            .IsRequired();
        builder.Property(item => item.ContentType)
            .HasColumnName("content_type")
            .HasMaxLength(100)
            .IsRequired();
        builder.Property(item => item.Length).HasColumnName("length");
        builder.Property(item => item.Sha256)
            .HasColumnName("sha256")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(item => item.Content)
            .HasColumnName("content")
            .IsRequired();
        builder.Property(item => item.UploadedAt).HasColumnName("uploaded_at");
        builder.HasIndex(item => new { item.WorkflowId, item.Sha256 }).IsUnique();
        builder.HasOne(item => item.Workflow)
            .WithMany(workflow => workflow.Evidence)
            .HasForeignKey(item => item.WorkflowId)
            .OnDelete(DeleteBehavior.Cascade);
    }
}
