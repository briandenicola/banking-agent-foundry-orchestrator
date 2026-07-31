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
            IWorkflowEvidenceService evidenceService,
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
            var evidence = await evidenceService.ListAsync(workflowId, cancellationToken);
            return Results.Ok(WorkflowDetailResponse.From(workflow, supportCase, evidence));
        }).RequireAuthorization("WorkflowInvoke");

        app.MapPost("/api/v1/workflows/{workflowId:guid}/evidence", async (
            [FromRoute] Guid workflowId,
            HttpRequest request,
            IWorkflowEvidenceService evidenceService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                if (!request.HasFormContentType)
                {
                    return Results.Problem(
                        title: "Evidence upload requires multipart form data",
                        statusCode: StatusCodes.Status400BadRequest);
                }

                var form = await request.ReadFormAsync(cancellationToken);
                if (form.Files.Count > WorkflowEvidenceService.MaximumFiles)
                {
                    throw new EvidenceValidationException(
                        $"A dispute workflow can contain at most {WorkflowEvidenceService.MaximumFiles} evidence files.");
                }

                var uploads = new List<WorkflowEvidenceUpload>(form.Files.Count);
                foreach (var file in form.Files)
                {
                    if (file.Length > WorkflowEvidenceService.MaximumFileBytes)
                    {
                        throw new EvidenceValidationException(
                            "Each evidence file must be 10 MB or smaller.");
                    }

                    await using var content = new MemoryStream((int)file.Length);
                    await file.CopyToAsync(content, cancellationToken);
                    uploads.Add(new WorkflowEvidenceUpload(file.FileName, content.ToArray()));
                }

                var added = await evidenceService.AddAsync(workflowId, uploads, cancellationToken);
                return Results.Ok(added.Select(item => new WorkflowEvidenceResponse(
                    item.Id,
                    item.FileName,
                    item.ContentType,
                    item.Length,
                    item.Sha256,
                    item.UploadedAt)));
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
                    title: "Evidence upload rejected",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status400BadRequest);
            }
        }).RequireAuthorization("WorkflowInvoke");

        app.MapGet("/api/v1/workflows/{workflowId:guid}/evidence/{evidenceId:guid}", async (
            [FromRoute] Guid workflowId,
            [FromRoute] Guid evidenceId,
            IWorkflowEvidenceService evidenceService,
            CancellationToken cancellationToken) =>
        {
            try
            {
                var evidence = await evidenceService.GetAsync(
                    workflowId,
                    evidenceId,
                    cancellationToken);
                return Results.File(
                    evidence.Content,
                    evidence.ContentType,
                    evidence.FileName,
                    enableRangeProcessing: true);
            }
            catch (EvidenceNotFoundException ex)
            {
                return Results.Problem(
                    title: "Evidence not found",
                    detail: ex.Message,
                    statusCode: StatusCodes.Status404NotFound);
            }
        }).RequireAuthorization("WorkflowInvoke");
    }
}
