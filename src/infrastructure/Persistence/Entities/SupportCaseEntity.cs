namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class SupportCaseEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public required string CaseNumber { get; set; }
    public required string Status { get; set; }
    public required string Summary { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public WorkflowEntity Workflow { get; set; } = null!;
}
