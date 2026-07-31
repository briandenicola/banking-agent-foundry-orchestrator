using System.Diagnostics;

namespace BankingAgent.Application;

public static class WorkflowTelemetry
{
    public const string ActivitySourceName = "BankingAgent.Workflow";
    public static readonly ActivitySource ActivitySource = new(ActivitySourceName);

    public static Activity? StartActivity(
        string name,
        Guid? workflowId = null,
        string? workflowTraceId = null)
    {
        var correlationId =
            Activity.Current?.GetTagItem("correlation.id")?.ToString() ??
            Activity.Current?.GetTagItem("correlation_id")?.ToString();
        var activity = ActivitySource.StartActivity(name, ActivityKind.Internal);
        if (workflowId is not null)
        {
            activity?.SetTag("workflow.id", workflowId.Value);
        }

        if (!string.IsNullOrWhiteSpace(workflowTraceId))
        {
            activity?.SetTag("workflow.trace_id", workflowTraceId);
        }

        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            activity?.SetTag("correlation.id", correlationId);
        }

        return activity;
    }

    public static string? GetCorrelationId() =>
        Activity.Current?.GetTagItem("correlation.id")?.ToString() ??
        Activity.Current?.GetTagItem("correlation_id")?.ToString();

    public static void RecordSuccess(Activity? activity, string outcome = "success")
    {
        activity?.SetTag("outcome", outcome);
        activity?.SetStatus(ActivityStatusCode.Ok);
    }

    public static void RecordFailure(Activity? activity, Exception exception, string outcome = "failure")
    {
        activity?.SetTag("outcome", outcome);
        activity?.SetTag("error.type", exception.GetType().Name);
        activity?.SetStatus(ActivityStatusCode.Error);
    }
}
