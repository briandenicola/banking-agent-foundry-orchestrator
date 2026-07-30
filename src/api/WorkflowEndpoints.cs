using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Routing;

namespace BankingAgent.Api;

public static class WorkflowEndpoints
{
    public static void MapWorkflowEndpoints(this IEndpointRouteBuilder app)
    {
        app.MapPost("/api/v1/workflows", async (
            [FromBody] WorkflowRequest request,
            IWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var workflow = await workflowService.StartAsync(request.UserMessage, cancellationToken);
                return Results.Ok(new WorkflowResponse(workflow.Id, workflow.TraceId, workflow.Status.ToString(), "Workflow accepted."));
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Workflow creation failed", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization("WorkflowInvoke");

        app.MapPost("/api/v1/workflows/{workflowId:guid}/approval", async (
            [FromRoute] Guid workflowId,
            [FromBody] ApprovalRequest request,
            IWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var workflow = await workflowService.ApproveAsync(workflowId, request.Decision, request.Reason, cancellationToken);
                return Results.Ok(new WorkflowResponse(workflow.Id, workflow.TraceId, workflow.Status.ToString(), "Approval recorded."));
            }
            catch (WorkflowNotFoundException ex)
            {
                return Results.Problem(
                    title: "Workflow not found",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status404NotFound);
            }
            catch (WorkflowConflictException ex)
            {
                return Results.Problem(
                    title: "Approval conflict",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status409Conflict);
            }
            catch (Exception ex)
            {
                return Results.Problem(title: "Approval failed", detail: ex.Message, statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization("WorkflowInvoke");

        app.MapGet("/api/v1/workflows/{workflowId:guid}", async (
            [FromRoute] Guid workflowId,
            IWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            var workflow = await workflowService.GetAsync(workflowId, cancellationToken);
            if (workflow is null)
            {
                return Results.Problem(
                    title: "Workflow not found",
                    detail: $"Workflow {workflowId} does not exist.",
                    statusCode: StatusCodes.Status404NotFound);
            }

            var supportCase = await workflowService.GetSupportCaseAsync(workflowId, cancellationToken);
            return Results.Ok(WorkflowDetailResponse.From(workflow, supportCase));
        }).RequireAuthorization("WorkflowInvoke");
    }
}
