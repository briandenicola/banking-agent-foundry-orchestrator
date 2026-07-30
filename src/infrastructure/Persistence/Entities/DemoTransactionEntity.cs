namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class DemoTransactionEntity
{
    public Guid Id { get; set; }
    public required string AccountReference { get; set; }
    public required string Merchant { get; set; }
    public decimal Amount { get; set; }
    public required string Currency { get; set; }
    public required string Status { get; set; }
    public DateTimeOffset OccurredAt { get; set; }
    public bool IsSuspicious { get; set; }
    public required string Description { get; set; }
}
