namespace BankingAgent.Domain;

public enum WorkflowStatus
{
    Draft,
    WaitingForApproval,
    Approved,
    Rejected,
    Completed,
    Failed
}

public sealed record WorkflowState(
    Guid Id,
    string TraceId,
    string UserMessage,
    WorkflowStatus Status,
    string? Intent,
    bool RequiresApproval,
    string? ApprovalDecision,
    string? ApprovalReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    IReadOnlyList<WorkflowEvent> Events);

public sealed record WorkflowEvent(
    string Type,
    string Message,
    DateTimeOffset Timestamp,
    string? Actor = null,
    string? Details = null);
