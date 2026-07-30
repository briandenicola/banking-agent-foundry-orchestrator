using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace BankingAgent.Infrastructure.Persistence.Configurations;

public sealed class DemoTransactionEntityConfiguration
    : IEntityTypeConfiguration<DemoTransactionEntity>
{
    public void Configure(EntityTypeBuilder<DemoTransactionEntity> builder)
    {
        builder.ToTable("demo_transactions");
        builder.HasKey(transaction => transaction.Id);
        builder.Property(transaction => transaction.Id).HasColumnName("id");
        builder.Property(transaction => transaction.AccountReference)
            .HasColumnName("account_reference")
            .HasMaxLength(64)
            .IsRequired();
        builder.Property(transaction => transaction.Merchant)
            .HasColumnName("merchant")
            .HasMaxLength(256)
            .IsRequired();
        builder.Property(transaction => transaction.Amount)
            .HasColumnName("amount")
            .HasPrecision(18, 2);
        builder.Property(transaction => transaction.Currency)
            .HasColumnName("currency")
            .HasMaxLength(3)
            .IsFixedLength()
            .IsRequired();
        builder.Property(transaction => transaction.Status)
            .HasColumnName("status")
            .HasMaxLength(32)
            .IsRequired();
        builder.Property(transaction => transaction.OccurredAt).HasColumnName("occurred_at");
        builder.Property(transaction => transaction.IsSuspicious).HasColumnName("is_suspicious");
        builder.Property(transaction => transaction.Description)
            .HasColumnName("description")
            .HasMaxLength(1024)
            .IsRequired();
    }
}
