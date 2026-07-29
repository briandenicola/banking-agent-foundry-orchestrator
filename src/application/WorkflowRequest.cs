namespace BankingAgent.Application;

public sealed record WorkflowRequest(string UserMessage);

public sealed record ApprovalRequest(string Decision, string Reason);

public sealed record WorkflowResponse(Guid WorkflowId, string TraceId, string Status, string Message);
