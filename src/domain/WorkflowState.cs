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
    IReadOnlyList<WorkflowEvent> Events,
    long Version = 0);

public sealed record WorkflowEvent(
    string Type,
    string Message,
    DateTimeOffset Timestamp,
    string? Actor = null,
    string? Details = null);

public enum ActionExecutionStatus
{
    Pending,
    Completed,
    Failed
}

public sealed record ApprovalDecision(
    Guid Id,
    Guid WorkflowId,
    string Decision,
    string Reason,
    string Actor,
    DateTimeOffset CreatedAt);

public sealed record ActionExecution(
    Guid Id,
    Guid WorkflowId,
    string ActionType,
    string IdempotencyKey,
    ActionExecutionStatus Status,
    DateTimeOffset RequestedAt,
    DateTimeOffset? CompletedAt = null,
    string? Result = null,
    string? ErrorCode = null);

public sealed record SupportCase(
    Guid Id,
    Guid WorkflowId,
    string CaseNumber,
    string Status,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorkflowEvidence(
    Guid Id,
    Guid WorkflowId,
    string FileName,
    string ContentType,
    long Length,
    string Sha256,
    byte[] Content,
    DateTimeOffset UploadedAt);
