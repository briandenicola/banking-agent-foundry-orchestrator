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

        builder.HasData(
            new DemoTransactionEntity
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000001"),
                AccountReference = "DEMO-CHECKING-001",
                Merchant = "Northwind Market",
                Amount = 84.27m,
                Currency = "USD",
                Status = "Settled",
                OccurredAt = new DateTimeOffset(2026, 7, 28, 16, 30, 0, TimeSpan.Zero),
                IsSuspicious = false,
                Description = "DEMO-TXN-1001 synthetic grocery purchase"
            },
            new DemoTransactionEntity
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000002"),
                AccountReference = "DEMO-CHECKING-001",
                Merchant = "Metro Transit",
                Amount = 12.40m,
                Currency = "USD",
                Status = "Pending",
                OccurredAt = new DateTimeOffset(2026, 7, 30, 8, 15, 0, TimeSpan.Zero),
                IsSuspicious = false,
                Description = "DEMO-TXN-1002 synthetic transit authorization"
            },
            new DemoTransactionEntity
            {
                Id = Guid.Parse("10000000-0000-0000-0000-000000000003"),
                AccountReference = "DEMO-CHECKING-001",
                Merchant = "Alpine Digital",
                Amount = 249.99m,
                Currency = "USD",
                Status = "Settled",
                OccurredAt = new DateTimeOffset(2026, 7, 29, 22, 5, 0, TimeSpan.Zero),
                IsSuspicious = true,
                Description = "DEMO-TXN-1003 synthetic card-not-present purchase"
            });
    }
}
