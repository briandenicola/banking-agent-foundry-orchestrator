using System.Collections.Concurrent;
using System.Diagnostics;
using BankingAgent.Domain;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using Xunit;

namespace BankingAgent.Application.Tests;

public sealed class WorkflowTelemetryTests
{
    [Fact]
    public async Task DisputeWorkflow_EmitsCorrelatedLifecycleAgentPersistenceAndApprovalSpansWithoutPii()
    {
        const string userMessage = "Account 1234: dispute this charge.";
        const string approvalReason = "Customer secret approval reason.";
        var activities = new ConcurrentBag<Activity>();
        using var listener = new ActivityListener
        {
            ShouldListenTo = source => source.Name == WorkflowTelemetry.ActivitySourceName,
            Sample = (ref ActivityCreationOptions<ActivityContext> _) =>
                ActivitySamplingResult.AllDataAndRecorded,
            ActivityStopped = activities.Add
        };
        ActivitySource.AddActivityListener(listener);

        WorkflowState? persisted = null;
        SupportCase? persistedSupportCase = null;
        var workflowRepository = new Mock<IWorkflowRepository>(MockBehavior.Strict);
        workflowRepository
            .Setup(repository => repository.AddAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, CancellationToken>((workflow, _) => persisted = workflow)
            .Returns(Task.CompletedTask);
        workflowRepository
            .Setup(repository => repository.UpdateAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, long, CancellationToken>((workflow, _, _) => persisted = workflow)
            .Returns(Task.CompletedTask);
        workflowRepository
            .Setup(repository => repository.GetAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persisted);

        var actionRepository = new Mock<IWorkflowActionRepository>(MockBehavior.Strict);
        actionRepository
            .Setup(repository => repository.RecordDecisionAsync(
                It.IsAny<WorkflowState>(),
                It.IsAny<ApprovalDecision>(),
                It.IsAny<ActionExecution>(),
                It.IsAny<SupportCase>(),
                It.IsAny<long>(),
                It.IsAny<CancellationToken>()))
            .Callback<WorkflowState, ApprovalDecision, ActionExecution?, SupportCase?, long, CancellationToken>(
                (workflow, _, _, supportCase, _, _) =>
                {
                    persisted = workflow;
                    persistedSupportCase = supportCase;
                })
            .Returns(Task.CompletedTask);
        actionRepository
            .Setup(repository => repository.GetSupportCaseAsync(
                It.IsAny<Guid>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(() => persistedSupportCase);

        var mcpClient = new Mock<IMcpClient>(MockBehavior.Strict);
        mcpClient
            .Setup(client => client.InvokeAsync(
                "workflow.plan",
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAgentResult(
                "workflow.plan",
                "workflow-planning",
                "dispute",
                "dispute-planning",
                requiresApproval: true));
        mcpClient
            .Setup(client => client.InvokeAsync(
                "dispute.plan",
                It.IsAny<IDictionary<string, object?>>(),
                It.IsAny<CancellationToken>()))
            .ReturnsAsync(CreateAgentResult(
                "dispute.plan",
                "dispute-planning",
                "dispute",
                null,
                requiresApproval: true));

        var service = new WorkflowService(
            mcpClient.Object,
            NullLogger<WorkflowService>.Instance,
            workflowRepository.Object,
            actionRepository.Object);

        // StartAsync persists Draft; RecoverAsync executes planner/specialist
        var draft = await service.StartAsync(userMessage);
        Assert.Equal(WorkflowStatus.Draft, draft.Status);
        var started = await service.RecoverAsync(draft.Id);
        var approved = await service.ApproveAsync(
            started.Id,
            "approve",
            approvalReason);

        Assert.Equal(WorkflowStatus.Completed, approved.Status);
        Assert.NotNull(persistedSupportCase);
        var invocationEvents = persisted!.Events
            .Where(workflowEvent =>
                workflowEvent.Details?.Contains("\"execution_mode\"", StringComparison.Ordinal) == true)
            .ToList();
        Assert.Equal(2, invocationEvents.Count);
        Assert.All(
            invocationEvents,
            workflowEvent =>
            {
                Assert.Contains("\"contract_version\":\"1.0\"", workflowEvent.Details);
                Assert.Contains("\"execution_mode\":\"model\"", workflowEvent.Details);
                Assert.DoesNotContain(userMessage, workflowEvent.Details, StringComparison.Ordinal);
            });

        var spans = activities
            .Where(activity =>
                activity.GetTagItem("workflow.id")?.ToString() == started.Id.ToString())
            .ToList();
        Assert.Contains(spans, activity => activity.OperationName == "workflow.lifecycle");
        Assert.Equal(2, spans.Count(activity => activity.OperationName == "hosted_agent.invoke"));
        Assert.All(
            spans.Where(activity => activity.OperationName == "hosted_agent.invoke"),
            activity =>
            {
                Assert.Equal("1.0", activity.GetTagItem("agent.contract_version")?.ToString());
                Assert.Equal("model", activity.GetTagItem("agent.execution_mode")?.ToString());
            });
        Assert.Contains(spans, activity => activity.OperationName == "persistence.workflow.add");
        Assert.Contains(spans, activity => activity.OperationName == "persistence.workflow.update");
        Assert.Contains(spans, activity => activity.OperationName == "workflow.approval");
        var approvalPersistence = Assert.Single(
            spans,
            activity => activity.OperationName == "persistence.approval.record");
        Assert.Equal(
            "dispute.support_case.create",
            approvalPersistence.GetTagItem("action.type")?.ToString());
        Assert.Equal("true", approvalPersistence.GetTagItem("support_case.created")?.ToString()?.ToLowerInvariant());

        var lifecycle = Assert.Single(
            spans,
            activity => activity.OperationName == "workflow.lifecycle");
        Assert.Equal(started.TraceId, lifecycle.TraceId.ToString());
        Assert.All(
            spans.Where(activity => activity.GetTagItem("workflow.id") is not null),
            activity => Assert.Equal(started.Id.ToString(), activity.GetTagItem("workflow.id")?.ToString()));

        var serializedTags = string.Join(
            " ",
            spans.SelectMany(activity => activity.TagObjects)
                .Select(tag => $"{tag.Key}={tag.Value}"));
        Assert.DoesNotContain(userMessage, serializedTags, StringComparison.Ordinal);
        Assert.DoesNotContain(approvalReason, serializedTags, StringComparison.Ordinal);
    }

    private static McpToolResult CreateAgentResult(
        string toolName,
        string agent,
        string intent,
        string? selectedAgent,
        bool requiresApproval)
    {
        var responseBody =
            $$"""
            {
              "contract_version": "1.0",
              "agent": "{{agent}}",
              "status": "ok",
              "execution_mode": "model",
              "intent": "{{intent}}",
              "summary": "Safe non-PII summary.",
              "requires_approval": {{requiresApproval.ToString().ToLowerInvariant()}},
              "selected_agent": {{(selectedAgent is null ? "null" : $"\"{selectedAgent}\"")}},
              "evidence": []
            }
            """;
        return new McpToolResult(
            toolName,
            "ok",
            "Agent completed.",
            new Dictionary<string, object?>
            {
                ["response_body"] = responseBody
            });
    }
}
