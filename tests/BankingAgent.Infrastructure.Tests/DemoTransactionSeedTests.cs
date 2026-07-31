using BankingAgent.Infrastructure.Persistence;
using BankingAgent.Infrastructure.Persistence.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Metadata;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class DemoTransactionSeedTests
{
    [Fact]
    public void Model_ContainsStableSyntheticTransactionsWithoutPii()
    {
        var options = new DbContextOptionsBuilder<BankingAgentDbContext>()
            .UseNpgsql("Host=localhost;Database=model_only;Username=model_only")
            .Options;
        using var context = new BankingAgentDbContext(options);
        var model = context.GetService<IDesignTimeModel>().Model;
        var entityType = model.FindEntityType(typeof(DemoTransactionEntity));

        Assert.NotNull(entityType);
        var seeds = entityType.GetSeedData().ToList();
        Assert.Equal(3, seeds.Count);
        Assert.All(
            seeds,
            seed =>
            {
                Assert.StartsWith("DEMO-", Assert.IsType<string>(seed[nameof(DemoTransactionEntity.AccountReference)]));
                Assert.StartsWith("DEMO-TXN-", Assert.IsType<string>(seed[nameof(DemoTransactionEntity.Description)]));
                Assert.DoesNotContain("@", string.Join(" ", seed.Values), StringComparison.Ordinal);
            });
    }
}
