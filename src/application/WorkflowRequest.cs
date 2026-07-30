using BankingAgent.Domain;

namespace BankingAgent.Application;

public sealed record WorkflowRequest(string UserMessage);

public sealed record ApprovalRequest(string Decision, string Reason);

public sealed record WorkflowResponse(Guid WorkflowId, string TraceId, string Status, string Message);

public sealed record WorkflowEventResponse(
    string Type,
    string Message,
    DateTimeOffset Timestamp,
    string? Actor,
    string? Details);

public sealed record SupportCaseResponse(
    Guid Id,
    string CaseNumber,
    string Status,
    string Summary,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public sealed record WorkflowDetailResponse(
    Guid WorkflowId,
    string TraceId,
    string UserMessage,
    string Status,
    string? Intent,
    bool RequiresApproval,
    string? ApprovalDecision,
    string? ApprovalReason,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    long Version,
    IReadOnlyList<WorkflowEventResponse> Events,
    SupportCaseResponse? SupportCase)
{
    public static WorkflowDetailResponse From(WorkflowState workflow, SupportCase? supportCase) =>
        new(
            WorkflowId: workflow.Id,
            TraceId: workflow.TraceId,
            UserMessage: workflow.UserMessage,
            Status: workflow.Status.ToString(),
            Intent: workflow.Intent,
            RequiresApproval: workflow.RequiresApproval,
            ApprovalDecision: workflow.ApprovalDecision,
            ApprovalReason: workflow.ApprovalReason,
            CreatedAt: workflow.CreatedAt,
            UpdatedAt: workflow.UpdatedAt,
            Version: workflow.Version,
            Events: workflow.Events
                .Select(e => new WorkflowEventResponse(e.Type, e.Message, e.Timestamp, e.Actor, e.Details))
                .ToList(),
            SupportCase: supportCase is null ? null : new SupportCaseResponse(
                supportCase.Id,
                supportCase.CaseNumber,
                supportCase.Status,
                supportCase.Summary,
                supportCase.CreatedAt,
                supportCase.UpdatedAt));
}
