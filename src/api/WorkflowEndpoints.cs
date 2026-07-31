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
            ValidateWorkflowRequest(request);
            var workflow = string.IsNullOrWhiteSpace(request.DemoScenario)
                ? await workflowService.StartAsync(request.UserMessage, cancellationToken)
                : await workflowService.StartDemoAsync(request.DemoScenario, cancellationToken);
            return Results.Ok(new WorkflowResponse(workflow.Id, workflow.TraceId, workflow.Status.ToString(), "Workflow accepted."));
        }).RequireAuthorization("WorkflowInvoke");

        app.MapPost("/api/v1/workflows/{workflowId:guid}/approval", async (
            [FromRoute] Guid workflowId,
            [FromBody] ApprovalRequest request,
            IWorkflowService workflowService,
            CancellationToken cancellationToken) =>
        {
            ValidateApprovalRequest(request);
            var workflow = await workflowService.ApproveAsync(
                workflowId,
                request.Decision,
                request.Reason,
                cancellationToken);
            return Results.Ok(new WorkflowResponse(workflow.Id, workflow.TraceId, workflow.Status.ToString(), "Approval recorded."));
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
                throw new WorkflowNotFoundException(workflowId);
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
            if (!request.HasFormContentType)
            {
                throw new RequestValidationException(new Dictionary<string, string[]>
                {
                    ["contentType"] = ["Evidence uploads require multipart form data."]
                });
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
        }).RequireAuthorization("WorkflowInvoke");

        app.MapGet("/api/v1/workflows/{workflowId:guid}/evidence/{evidenceId:guid}", async (
            [FromRoute] Guid workflowId,
            [FromRoute] Guid evidenceId,
            IWorkflowEvidenceService evidenceService,
            CancellationToken cancellationToken) =>
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
        }).RequireAuthorization("WorkflowInvoke");
    }

    private static void ValidateWorkflowRequest(WorkflowRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (string.IsNullOrWhiteSpace(request.UserMessage))
        {
            errors["userMessage"] = ["A workflow message is required."];
        }
        else if (request.UserMessage.Length > 4000)
        {
            errors["userMessage"] = ["A workflow message must be 4,000 characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }

    private static void ValidateApprovalRequest(ApprovalRequest request)
    {
        var errors = new Dictionary<string, string[]>();
        if (!string.Equals(request.Decision, "approve", StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(request.Decision, "reject", StringComparison.OrdinalIgnoreCase))
        {
            errors["decision"] = ["Decision must be either 'approve' or 'reject'."];
        }

        if (string.IsNullOrWhiteSpace(request.Reason))
        {
            errors["reason"] = ["An approval reason is required."];
        }
        else if (request.Reason.Length > 1000)
        {
            errors["reason"] = ["An approval reason must be 1,000 characters or fewer."];
        }

        if (errors.Count > 0)
        {
            throw new RequestValidationException(errors);
        }
    }
}
