using BankingAgent.Domain;

namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class ActionExecutionEntity
{
    public Guid Id { get; set; }
    public Guid WorkflowId { get; set; }
    public required string ActionType { get; set; }
    public required string IdempotencyKey { get; set; }
    public ActionExecutionStatus Status { get; set; }
    public DateTimeOffset RequestedAt { get; set; }
    public DateTimeOffset? CompletedAt { get; set; }
    public string? Result { get; set; }
    public string? ErrorCode { get; set; }
    public WorkflowEntity Workflow { get; set; } = null!;
}
