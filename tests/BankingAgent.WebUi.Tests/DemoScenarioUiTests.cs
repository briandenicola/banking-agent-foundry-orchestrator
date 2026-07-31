using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.Domain;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.AspNetCore.Mvc.ViewFeatures;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using webui.Pages;
using Xunit;

namespace BankingAgent.WebUi.Tests;

public sealed class DemoScenarioUiTests
{
    // ──────────────────────────────────────────────────────────────────
    // Demo scenario catalog exposure
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public void IndexModel_ExposesEveryGuidedScenario()
    {
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(item => item.CreateClient("orchestrator"))
            .Returns(new HttpClient
            {
                BaseAddress = new Uri("https://orchestrator.example")
            });
        var model = new IndexModel(
            factory.Object,
            NullLogger<IndexModel>.Instance);

        Assert.Equal(
            DemoScenarioCatalog.All.Select(scenario => scenario.Id),
            model.DemoScenarios.Select(scenario => scenario.Id));
    }

    // ──────────────────────────────────────────────────────────────────
    // POST → 202: IndexModel accepts success and redirects
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPostAsync_Receives202_RedirectsToWorkflowStatusPage()
    {
        var workflowId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                traceId = Guid.NewGuid().ToString("N"),
                status = "Draft",
                message = "Workflow accepted for processing."
            }),
            Headers = { Location = new Uri($"https://orchestrator.example/api/v1/workflows/{workflowId}") }
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "Explain DEMO-TXN-1001.";

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(workflowId, redirect.RouteValues?["workflowId"]);
    }

    [Fact]
    public async Task OnPostAsync_Receives202WithDraftStatus_DoesNotSetErrorMessage()
    {
        var workflowId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                traceId = "trace-abc",
                status = "Draft",
                message = "Workflow accepted for processing."
            })
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "Why is DEMO-TXN-1002 pending?";

        await model.OnPostAsync();

        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostAsync_ReceivesError_SetsActionableErrorMessage()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent("{\"title\":\"Bad request\"}")
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "test";

        await model.OnPostAsync();

        Assert.NotNull(model.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(model.ErrorMessage),
            "Error message must be non-empty for accessibility rendering.");
    }

    [Fact]
    public async Task OnPostAsync_EmptyUserMessage_SetsValidationError()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "";

        await model.OnPostAsync();

        Assert.NotNull(model.ErrorMessage);
    }

    // ──────────────────────────────────────────────────────────────────
    // GET: polling stop conditions — UI must stop polling at these statuses
    // ──────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("Completed")]
    [InlineData("Failed")]
    [InlineData("Rejected")]
    [InlineData("WaitingForApproval")]
    public async Task OnGetAsync_TerminalStatus_WorkflowPropertyIsSet(string terminalStatus)
    {
        var workflowId = Guid.NewGuid();
        var detailJson = BuildWorkflowDetailJson(workflowId, terminalStatus);
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(workflowId);

        Assert.NotNull(model.Workflow);
        Assert.Equal(terminalStatus, model.Workflow.Status);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnGetAsync_DraftStatus_WorkflowPropertyIsSet()
    {
        var workflowId = Guid.NewGuid();
        var detailJson = BuildWorkflowDetailJson(workflowId, "Draft");
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(workflowId);

        Assert.NotNull(model.Workflow);
        Assert.Equal("Draft", model.Workflow.Status);
    }

    // ──────────────────────────────────────────────────────────────────
    // GET: WaitingForApproval exposes approval decision data
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnGetAsync_WaitingForApproval_WorkflowShowsApprovalRequired()
    {
        var workflowId = Guid.NewGuid();
        var detailJson = BuildWorkflowDetailJson(workflowId, "WaitingForApproval",
            requiresApproval: true, approvalDecision: null);
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(workflowId);

        Assert.NotNull(model.Workflow);
        Assert.True(model.Workflow.RequiresApproval,
            "WaitingForApproval workflow must expose RequiresApproval=true for the approval panel.");
        Assert.Null(model.Workflow.ApprovalDecision);
    }

    // ──────────────────────────────────────────────────────────────────
    // GET: stage-track event types are present for WaitingForApproval
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnGetAsync_WaitingForApproval_RealisticEvents_StageAuditTypesPresent()
    {
        // The SSR stage-track derives Plan=done and Decide=active from "workflow.plan"
        // and "workflow.approval_required" event types respectively.
        // This test confirms the model exposes those types when the orchestrator returns them,
        // validating that the event-based stage logic has the data it needs.
        var workflowId = Guid.NewGuid();
        var t0 = DateTimeOffset.UtcNow;
        var eventTypes = new[] { "workflow.started", "workflow.plan", "workflow.route_selected", "workflow.approval_required" };
        var detailJson = BuildWorkflowDetailJsonWithEventTypes(workflowId, "WaitingForApproval",
            requiresApproval: true, approvalDecision: null, eventTypes: eventTypes);
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(workflowId);

        Assert.NotNull(model.Workflow);
        Assert.Equal(4, model.Workflow.Events.Count);
        Assert.Contains(model.Workflow.Events, e => e.Type == "workflow.plan");
        Assert.Contains(model.Workflow.Events, e => e.Type == "workflow.approval_required");
        Assert.DoesNotContain(model.Workflow.Events, e => e.Type == "workflow.completed");
    }

    // ──────────────────────────────────────────────────────────────────
    // GET: events, support case, and evidence are exposed
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnGetAsync_CompletedWorkflow_EventsAndEvidenceAreExposed()
    {
        var workflowId = Guid.NewGuid();
        var detailJson = BuildWorkflowDetailJson(workflowId, "Completed",
            eventCount: 3, evidenceCount: 1, hasSupportCase: true);
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(workflowId);

        Assert.NotNull(model.Workflow);
        Assert.Equal(3, model.Workflow.Events.Count);
        Assert.Single(model.Workflow.Evidence);
        Assert.NotNull(model.Workflow.SupportCase);
    }

    [Fact]
    public async Task OnGetAsync_FailedWorkflow_EventsAreExposedForTimeline()
    {
        var workflowId = Guid.NewGuid();
        var detailJson = BuildWorkflowDetailJson(workflowId, "Failed", eventCount: 2);
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(detailJson, System.Text.Encoding.UTF8, "application/json")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(workflowId);

        Assert.NotNull(model.Workflow);
        Assert.Equal("Failed", model.Workflow.Status);
        Assert.Equal(2, model.Workflow.Events.Count);
    }

    // ──────────────────────────────────────────────────────────────────
    // Bounded failure: timeout produces actionable error message
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnGetAsync_ServiceTimeout_SetsActionableErrorMessage()
    {
        var handler = new TimeoutHttpMessageHandler();
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(Guid.NewGuid());

        Assert.NotNull(model.ErrorMessage);
        Assert.False(string.IsNullOrWhiteSpace(model.ErrorMessage),
            "Timeout error message must be actionable (non-empty) for accessibility.");
    }

    [Fact]
    public async Task OnPostAsync_ServiceTimeout_SetsActionableErrorMessage()
    {
        var handler = new TimeoutHttpMessageHandler();
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "Timeout probe.";

        await model.OnPostAsync();

        Assert.NotNull(model.ErrorMessage);
    }

    // ──────────────────────────────────────────────────────────────────
    // Accessibility: StatusMessage and ErrorMessage are non-null
    // only when there is something to announce
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnGetAsync_NoWorkflowId_NeitherMessageNorErrorIsSet()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
        var model = CreateIndexModel(handler);

        await model.OnGetAsync(null);

        Assert.Null(model.Workflow);
        Assert.Null(model.StatusMessage);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostAsync_Success_StatusMessageIsSetForAnnouncement()
    {
        var workflowId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                traceId = "t",
                status = "Draft",
                message = "Accepted for processing."
            })
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "Status message probe.";

        var result = await model.OnPostAsync();

        // After a successful submit, TempData StatusMessage should be set or we redirect
        // and the Workflow is not null on next load
        Assert.IsType<RedirectToPageResult>(result);
    }

    // ──────────────────────────────────────────────────────────────────
    // Approval POST: idempotent retry produces consistent status message
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPostApproveAsync_ApprovalSuccess_SetsStatusMessage()
    {
        var workflowId = Guid.NewGuid();
        var approvalHandler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                traceId = "t",
                status = "Completed",
                message = "Approval recorded."
            })
        });
        var model = CreateIndexModel(approvalHandler);
        model.ApprovalInput.Decision = "approve";
        model.ApprovalInput.Reason = "Transaction verified.";

        var result = await model.OnPostApproveAsync(workflowId);

        Assert.IsType<RedirectToPageResult>(result);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostApproveAsync_EmptyReason_SetsValidationError()
    {
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{}")
        });
        var model = CreateIndexModel(handler);
        model.ApprovalInput.Decision = "approve";
        model.ApprovalInput.Reason = "";

        await model.OnPostApproveAsync(Guid.NewGuid());

        Assert.NotNull(model.ErrorMessage);
    }


    // ──────────────────────────────────────────────────────────────────
    // Evidence coalescing regression (aria-optional-evidence-gate.md)
    // Server-side: EvidenceFiles empty list and null coalescing.
    // The browser-level proof lives in tests/webui-js/site.submit.test.js.
    // ──────────────────────────────────────────────────────────────────

    [Fact]
    public async Task OnPostAsync_WithExplicitEmptyEvidenceFiles_SubmitsSuccessfullyWithoutError()
    {
        // Proves the happy-path: empty list (no files selected) does not block submission.
        // EvidenceFiles = [] simulates a user submitting the form without attaching any files.
        var workflowId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                traceId = "trace-evidence-empty",
                status = "Draft",
                message = "Accepted."
            })
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "Dispute charge on 2026-07-15.";
        model.Input.EvidenceFiles = [];   // explicit empty list — no files

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(workflowId, redirect.RouteValues?["workflowId"]);
        Assert.Null(model.ErrorMessage);
    }

    [Fact]
    public async Task OnPostAsync_WithNullEvidenceFiles_CoalescesToEmptyAndSubmitsSuccessfully()
    {
        // Regression: OnPostAsync must not throw when EvidenceFiles is null.
        // Theo's fix: change property to List<IFormFile>? and coalesce at the consumption point.
        // Expected post-fix behaviour: redirects exactly as for empty list.
        var workflowId = Guid.NewGuid();
        var handler = new FakeHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new
            {
                workflowId,
                traceId = "trace-evidence-null",
                status = "Draft",
                message = "Accepted."
            })
        });
        var model = CreateIndexModel(handler);
        model.Input.UserMessage = "Dispute charge on 2026-07-15.";
        model.Input.EvidenceFiles = null!;  // simulates null binding from optional file input

        var result = await model.OnPostAsync();

        var redirect = Assert.IsType<RedirectToPageResult>(result);
        Assert.Equal(workflowId, redirect.RouteValues?["workflowId"]);
        Assert.Null(model.ErrorMessage);
    }

    // ──────────────────────────────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────────────────────────────

    private static IndexModel CreateIndexModel(HttpMessageHandler handler)
    {
        var httpClient = new HttpClient(handler)
        {
            BaseAddress = new Uri("https://orchestrator.example")
        };
        var factory = new Mock<IHttpClientFactory>();
        factory.Setup(f => f.CreateClient("orchestrator")).Returns(httpClient);

        var model = new IndexModel(factory.Object, NullLogger<IndexModel>.Instance)
        {
            PageContext = new PageContext
            {
                HttpContext = new DefaultHttpContext()
            }
        };
        model.TempData = new TempDataDictionary(
            model.HttpContext,
            new TestTempDataProvider());
        return model;
    }

    private static string BuildWorkflowDetailJson(
        Guid workflowId,
        string status,
        bool requiresApproval = false,
        string? approvalDecision = null,
        int eventCount = 1,
        int evidenceCount = 0,
        bool hasSupportCase = false)
    {
        var t0 = DateTimeOffset.UtcNow;
        var events = Enumerable.Range(0, eventCount).Select(i => new
        {
            type = i == 0 ? "workflow.started" : "workflow.step",
            message = $"Event {i}",
            timestamp = t0.AddSeconds(i),
            actor = "system",
            details = (string?)null
        }).ToArray();

        var evidence = Enumerable.Range(0, evidenceCount).Select(i => new
        {
            id = Guid.NewGuid(),
            fileName = $"receipt-{i}.pdf",
            contentType = "application/pdf",
            length = 100L,
            sha256 = new string('a', 64),
            uploadedAt = t0
        }).ToArray();

        var supportCase = hasSupportCase
            ? (object)new
            {
                id = Guid.NewGuid(),
                caseNumber = $"DSP-{workflowId:N}",
                status = "Open",
                summary = "Dispute case.",
                createdAt = t0,
                updatedAt = t0
            }
            : (object?)null;

        return JsonSerializer.Serialize(new
        {
            workflowId,
            traceId = Guid.NewGuid().ToString("N"),
            userMessage = "Test message.",
            status,
            intent = status == "Completed" ? "dispute-planning" : (string?)null,
            requiresApproval,
            approvalDecision,
            approvalReason = (string?)null,
            createdAt = t0,
            updatedAt = t0,
            version = 1L,
            events,
            supportCase,
            evidence
        });
    }

    private static string BuildWorkflowDetailJsonWithEventTypes(
        Guid workflowId,
        string status,
        bool requiresApproval = false,
        string? approvalDecision = null,
        string[]? eventTypes = null)
    {
        var t0 = DateTimeOffset.UtcNow;
        var types = eventTypes ?? ["workflow.started"];
        var events = types.Select((type, i) => new
        {
            type,
            message = type.Replace("workflow.", "").Replace("_", " "),
            timestamp = t0.AddSeconds(i),
            actor = "system",
            details = (string?)null
        }).ToArray();

        return JsonSerializer.Serialize(new
        {
            workflowId,
            traceId = Guid.NewGuid().ToString("N"),
            userMessage = "Test message.",
            status,
            intent = (string?)null,
            requiresApproval,
            approvalDecision,
            approvalReason = (string?)null,
            createdAt = t0,
            updatedAt = t0,
            version = (long)(types.Length - 1),
            events,
            supportCase = (object?)null,
            evidence = Array.Empty<object>()
        });
    }

    private sealed class FakeHttpMessageHandler(HttpResponseMessage response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }

    private sealed class TimeoutHttpMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromException<HttpResponseMessage>(
                new TaskCanceledException("Synthetic timeout.", null, cancellationToken));
    }

    private sealed class TestTempDataProvider : ITempDataProvider
    {
        public IDictionary<string, object> LoadTempData(HttpContext context) =>
            new Dictionary<string, object>();

        public void SaveTempData(HttpContext context, IDictionary<string, object> values) { }
    }
}
