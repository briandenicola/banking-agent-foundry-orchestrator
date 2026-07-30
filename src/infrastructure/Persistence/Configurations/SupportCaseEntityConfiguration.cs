using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class SupportCaseEntityConfiguration
    : IEntityTypeConfiguration<SupportCaseEntity>
{
    public void Configure(EntityTypeBuilder<SupportCaseEntity> builder)
    {
        builder.ToTable("support_cases");
        builder.HasKey(supportCase => supportCase.Id);
        builder.Property(supportCase => supportCase.Id).HasColumnName("id");
        builder.Property(supportCase => supportCase.WorkflowId).HasColumnName("workflow_id");
        builder.HasIndex(supportCase => supportCase.WorkflowId).IsUnique();
        builder.Property(supportCase => supportCase.CaseNumber)
            .HasColumnName("case_number")
            .HasMaxLength(64)
            .IsRequired();
        builder.HasIndex(supportCase => supportCase.CaseNumber).IsUnique();
        builder.Property(supportCase => supportCase.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(supportCase => supportCase.Summary)
            .HasColumnName("summary")
            .HasMaxLength(1024)
            .IsRequired();
        builder.Property(supportCase => supportCase.CreatedAt).HasColumnName("created_at");
        builder.Property(supportCase => supportCase.UpdatedAt).HasColumnName("updated_at");
    }
}
