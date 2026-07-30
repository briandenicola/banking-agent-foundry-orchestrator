namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class WorkflowEventEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public int Sequence { get; set; }
    public required string Type { get; set; }
    public required string Message { get; set; }
    public DateTimeOffset Timestamp { get; set; }
    public string? Actor { get; set; }
    public string? Details { get; set; }
    public WorkflowEntity Workflow { get; set; } = null!;
}
