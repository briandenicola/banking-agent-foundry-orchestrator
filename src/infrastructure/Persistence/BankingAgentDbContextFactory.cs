using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace BankingAgent.Infrastructure.Persistence;

public sealed class BankingAgentDbContextFactory
    : IDesignTimeDbContextFactory<BankingAgentDbContext>
{
    public BankingAgentDbContext CreateDbContext(string[] args)
    {
        var options = new DbContextOptionsBuilder<BankingAgentDbContext>()
            .UseNpgsql("Host=localhost;Database=banking_agent;Username=postgres")
            .Options;

        return new BankingAgentDbContext(options);
    }
}
