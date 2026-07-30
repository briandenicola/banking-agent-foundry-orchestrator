namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class ApprovalDecisionEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public required string Decision { get; set; }
    public required string Reason { get; set; }
    public required string Actor { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public WorkflowEntity Workflow { get; set; } = null!;
}
