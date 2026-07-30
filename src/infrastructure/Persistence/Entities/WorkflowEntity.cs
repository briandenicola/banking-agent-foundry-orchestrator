using BankingAgent.Domain;

namespace BankingAgent.Infrastructure.Persistence.Entities;

public sealed class WorkflowEntity
{
    public Guid Id { get; set; }
    public required string TraceId { get; set; }
    public required string UserMessage { get; set; }
    public WorkflowStatus Status { get; set; }
    public string? Intent { get; set; }
    public bool RequiresApproval { get; set; }
    public string? ApprovalDecision { get; set; }
    public string? ApprovalReason { get; set; }
    public DateTimeOffset CreatedAt { get; set; }
    public DateTimeOffset UpdatedAt { get; set; }
    public long Version { get; set; }
    public ICollection<WorkflowEventEntity> Events { get; } = [];
    public ICollection<ApprovalDecisionEntity> Decisions { get; } = [];
    public ICollection<ActionExecutionEntity> ActionExecutions { get; } = [];
    public SupportCaseEntity? SupportCase { get; set; }
}
