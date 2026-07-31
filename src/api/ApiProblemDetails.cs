using BankingAgent.Domain;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace BankingAgent.Api;

public static class ApiProblemDetails
{
    public static IServiceCollection AddBankingAgentProblemDetails(this IServiceCollection services)
    {
        services.AddProblemDetails(options =>
        {
            options.CustomizeProblemDetails = context =>
            {
                context.ProblemDetails.Extensions["traceId"] =
                    context.HttpContext.TraceIdentifier;
            };
        });
        services.AddExceptionHandler<BankingAgentExceptionHandler>();
        return services;
    }

    public static IApplicationBuilder UseBankingAgentProblemDetails(this IApplicationBuilder app)
    {
        app.UseExceptionHandler();
        app.UseStatusCodePages(async statusCodeContext =>
        {
            var response = statusCodeContext.HttpContext.Response;
            var descriptor = response.StatusCode switch
            {
                StatusCodes.Status401Unauthorized => new ProblemDescriptor(
                    "authentication_required",
                    "Authentication required",
                    "A valid bearer token is required."),
                StatusCodes.Status403Forbidden => new ProblemDescriptor(
                    "access_forbidden",
                    "Access forbidden",
                    "The authenticated caller does not have permission to perform this operation."),
                StatusCodes.Status404NotFound => new ProblemDescriptor(
                    "route_not_found",
                    "Resource not found",
                    "The requested resource does not exist."),
                _ => null
            };

            if (descriptor is null)
            {
                return;
            }

            var problemDetailsService = statusCodeContext.HttpContext.RequestServices
                .GetRequiredService<IProblemDetailsService>();
            await problemDetailsService.WriteAsync(new ProblemDetailsContext
            {
                HttpContext = statusCodeContext.HttpContext,
                ProblemDetails = CreateProblemDetails(response.StatusCode, descriptor)
            });
        });
        return app;
    }

    internal static ProblemDetails CreateProblemDetails(
        int status,
        ProblemDescriptor descriptor)
    {
        var problem = new ProblemDetails
        {
            Status = status,
            Title = descriptor.Title,
            Detail = descriptor.Detail,
            Type = $"https://banking-agent.dev/problems/{descriptor.Code}"
        };
        problem.Extensions["code"] = descriptor.Code;
        return problem;
    }

    internal sealed record ProblemDescriptor(string Code, string Title, string Detail);
}

public sealed class BankingAgentExceptionHandler(
    IProblemDetailsService problemDetailsService,
    ILogger<BankingAgentExceptionHandler> logger) : IExceptionHandler
{
    public async ValueTask<bool> TryHandleAsync(
        HttpContext httpContext,
        Exception exception,
        CancellationToken cancellationToken)
    {
        if (httpContext.RequestAborted.IsCancellationRequested)
        {
            return false;
        }

        var (status, descriptor) = MapException(exception);
        LogFailure(httpContext, exception, status);

        var problem = ApiProblemDetails.CreateProblemDetails(status, descriptor);
        if (exception is RequestValidationException validationException)
        {
            problem.Extensions["errors"] = validationException.Errors;
        }

        httpContext.Response.StatusCode = status;
        await problemDetailsService.WriteAsync(new ProblemDetailsContext
        {
            HttpContext = httpContext,
            ProblemDetails = problem,
            Exception = exception
        });
        return true;
    }

    private static (int Status, ApiProblemDetails.ProblemDescriptor Descriptor) MapException(
        Exception exception) =>
        exception switch
        {
            RequestValidationException => (
                StatusCodes.Status400BadRequest,
                new("validation_failed", "Request validation failed", "Correct the invalid request values and try again.")),
            BadHttpRequestException => (
                StatusCodes.Status400BadRequest,
                new("invalid_request", "Invalid request", "The request body or headers are invalid.")),
            JsonException => (
                StatusCodes.Status400BadRequest,
                new("invalid_request", "Invalid request", "The JSON request body is invalid.")),
            EvidenceValidationException => (
                StatusCodes.Status400BadRequest,
                new("evidence_invalid", "Evidence upload rejected", exception.Message)),
            WorkflowNotFoundException => (
                StatusCodes.Status404NotFound,
                new("workflow_not_found", "Workflow not found", exception.Message)),
            EvidenceNotFoundException => (
                StatusCodes.Status404NotFound,
                new("evidence_not_found", "Evidence not found", exception.Message)),
            EvidenceNotAllowedException => (
                StatusCodes.Status409Conflict,
                new("evidence_not_allowed", "Evidence not allowed", exception.Message)),
            WorkflowConflictException => (
                StatusCodes.Status409Conflict,
                new("workflow_conflict", "Workflow conflict", exception.Message)),
            TimeoutException => (
                StatusCodes.Status504GatewayTimeout,
                new("dependency_timeout", "Dependency timeout", "A required downstream service did not respond in time.")),
            TaskCanceledException => (
                StatusCodes.Status504GatewayTimeout,
                new("dependency_timeout", "Dependency timeout", "A required downstream service did not respond in time.")),
            HttpRequestException => (
                StatusCodes.Status502BadGateway,
                new("dependency_unavailable", "Dependency unavailable", "A required downstream service could not complete the request.")),
            _ => (
                StatusCodes.Status500InternalServerError,
                new("internal_error", "Internal server error", "The request could not be completed."))
        };

    private void LogFailure(HttpContext context, Exception exception, int status)
    {
        if (status >= StatusCodes.Status500InternalServerError)
        {
            logger.LogError(
                "API request failed with {StatusCode} and exception type {ExceptionType}. Path={Path}",
                status,
                exception.GetType().Name,
                context.Request.Path);
            return;
        }

        logger.LogWarning(
            "API request rejected with {StatusCode} and exception type {ExceptionType}. Path={Path}",
            status,
            exception.GetType().Name,
            context.Request.Path);
    }
}
