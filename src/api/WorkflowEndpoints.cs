using BankingAgent.Application;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BankingAgent.Api;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/workflows", async ([FromBody] WorkflowRequest request, IWorkflowService workflowService, CancellationToken cancellationToken) =>
        {
            var workflow = await workflowService.StartAsync(request.UserMessage, cancellationToken);
            return Results.Ok(new WorkflowResponse(workflow.Id, workflow.TraceId, workflow.Status.ToString(), "Workflow accepted."));
        });

        app.MapPost("/api/v1/workflows/{workflowId:guid}/approval", async ([FromRoute] Guid workflowId, [FromBody] ApprovalRequest request, IWorkflowService workflowService, CancellationToken cancellationToken) =>
        {
            var workflow = await workflowService.ApproveAsync(workflowId, request.Decision, request.Reason, cancellationToken);
            return Results.Ok(new WorkflowResponse(workflow.Id, workflow.TraceId, workflow.Status.ToString(), "Approval recorded."));
        });
    }
}
