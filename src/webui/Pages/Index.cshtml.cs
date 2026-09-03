using System.Net.Http.Json;
using System.Text.Json;
using BankingAgent.Application;
using BankingAgent.WebUi;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace webui.Pages;

public class IndexModel : PageModel
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<IndexModel> _logger;
    private readonly ISignedInCustomerAccessor _signedInCustomer;

    public IndexModel(
        IHttpClientFactory httpClientFactory,
        ILogger<IndexModel> logger,
        ISignedInCustomerAccessor signedInCustomer)
    {
        _httpClient = httpClientFactory.CreateClient("orchestrator");
        _logger = logger;
        _signedInCustomer = signedInCustomer;
    }

    public SignedInCustomer Customer => _signedInCustomer.Current;

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public ApprovalInputModel ApprovalInput { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public WorkflowDetailResponse? Workflow { get; private set; }
    public IReadOnlyList<DemoScenarioDefinition> DemoScenarios => DemoScenarioCatalog.All;

    public async Task OnGetAsync(Guid? workflowId)
    {
        if (workflowId.HasValue)
        {
            await LoadWorkflowAsync(workflowId.Value);
        }
    }

    /// <summary>
    /// Polling endpoint called by the JavaScript polling loop.
    /// Returns JSON with the current workflow state for client-side rendering.
    /// </summary>
    public async Task<IActionResult> OnGetPollAsync(Guid workflowId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/v1/workflows/{workflowId}");
            if (response.StatusCode == System.Net.HttpStatusCode.NotFound)
            {
                return new JsonResult(new { error = "not_found" }) { StatusCode = 404 };
            }

            if (!response.IsSuccessStatusCode)
            {
                var msg = await ReadFailureAsync(response, "Workflow status unavailable.");
                return new JsonResult(new { error = msg }) { StatusCode = (int)response.StatusCode };
            }

            var workflow = await response.Content.ReadFromJsonAsync<WorkflowDetailResponse>();
            if (workflow is null)
            {
                return new JsonResult(new { error = "Invalid response from workflow service." }) { StatusCode = 502 };
            }

            return new JsonResult(workflow);
        }
        catch (HttpRequestException ex)
        {
            _logger.LogError(ex, "Poll request for {WorkflowId} failed", workflowId);
            return new JsonResult(new { error = "Service temporarily unavailable." }) { StatusCode = 503 };
        }
        catch (TaskCanceledException ex)
        {
            _logger.LogError(ex, "Poll request for {WorkflowId} timed out", workflowId);
            return new JsonResult(new { error = "Request timed out." }) { StatusCode = 504 };
        }
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.UserMessage))
        {
            ErrorMessage = "Enter a banking request before starting a workflow.";
            return Page();
        }

        try
        {
            var files = Input.EvidenceFiles ?? [];
            var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/workflows",
                new
                {
                    userMessage = Input.UserMessage,
                    demoScenario = Input.DemoScenario,
                    expectsEvidence = files.Count > 0,
                    // Taken from the platform-verified identity rather than
                    // anything the browser submitted, so a customer cannot ask
                    // for a workflow personalised with someone else's profile.
                    customerId = Customer.IsAuthenticated ? Customer.Id : null
                });

            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = await ReadFailureAsync(response, "The workflow service rejected the request.");
                return Page();
            }

            var workflow = await response.Content.ReadFromJsonAsync<WorkflowResponse>();
            if (workflow is null)
            {
                ErrorMessage = "The workflow service returned an invalid response.";
                return Page();
            }

            // Upload evidence before triggering execution if files were provided.
            // The API defers the execution trigger until after evidence upload so
            // the specialist always sees the complete evidence set.
            if (files.Count > 0)
            {
                var uploadError = await UploadEvidenceAsync(workflow.WorkflowId, files);
                if (uploadError is not null)
                {
                    ErrorMessage = uploadError;
                    return RedirectToPage(new { workflowId = workflow.WorkflowId });
                }
            }

            StatusMessage = files.Count > 0
                ? "Workflow submitted with supporting evidence. Processing started."
                : "Workflow submitted. Processing started.";
            return RedirectToPage(new { workflowId = workflow.WorkflowId });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "The orchestrator API could not be reached");
            ErrorMessage = "The workflow service is currently unavailable. Try again shortly.";
            return Page();
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogError(exception, "The orchestrator API request timed out");
            ErrorMessage = "The workflow request timed out. Check its status before resubmitting.";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid workflowId)
    {
        if (ApprovalInput.Decision is not ("approve" or "reject")
            || string.IsNullOrWhiteSpace(ApprovalInput.Reason))
        {
            ErrorMessage = "Choose a decision and provide a reason.";
            await LoadWorkflowAsync(workflowId);
            return Page();
        }

        if (workflowId == Guid.Empty)
        {
            ErrorMessage = "Create a workflow before recording a decision.";
            return Page();
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/workflows/{workflowId}/approval",
                new { decision = ApprovalInput.Decision, reason = ApprovalInput.Reason });
            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = await ReadFailureAsync(response, "The approval request was rejected.");
                await LoadWorkflowAsync(workflowId);
                return Page();
            }

            StatusMessage = "Approval recorded successfully.";
            return RedirectToPage(new { workflowId });
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "The orchestrator API could not be reached for approval");
            ErrorMessage = "The approval service is currently unavailable.";
            await LoadWorkflowAsync(workflowId);
            return Page();
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogError(exception, "The orchestrator approval request timed out");
            ErrorMessage = "The approval request timed out. Refresh the workflow before retrying.";
            await LoadWorkflowAsync(workflowId);
            return Page();
        }
    }

    public async Task<IActionResult> OnGetEvidenceAsync(Guid workflowId, Guid evidenceId)
    {
        try
        {
            var response = await _httpClient.GetAsync(
                $"/api/v1/workflows/{workflowId}/evidence/{evidenceId}");
            if (!response.IsSuccessStatusCode)
            {
                return NotFound();
            }

            var content = await response.Content.ReadAsByteArrayAsync();
            var contentType = response.Content.Headers.ContentType?.MediaType
                ?? "application/octet-stream";
            var fileName = response.Content.Headers.ContentDisposition?.FileNameStar
                ?? response.Content.Headers.ContentDisposition?.FileName?.Trim('"')
                ?? "evidence";
            return File(content, contentType, fileName);
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(
                exception,
                "Evidence {EvidenceId} for workflow {WorkflowId} could not be loaded",
                evidenceId,
                workflowId);
            return NotFound();
        }
    }

    private async Task LoadWorkflowAsync(Guid workflowId)
    {
        try
        {
            var response = await _httpClient.GetAsync($"/api/v1/workflows/{workflowId}");
            if (!response.IsSuccessStatusCode)
            {
                ErrorMessage = await ReadFailureAsync(response, "The workflow could not be loaded.");
                return;
            }

            Workflow = await response.Content.ReadFromJsonAsync<WorkflowDetailResponse>();
            if (Workflow is null)
            {
                ErrorMessage = "The workflow service returned an invalid status response.";
            }
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "Workflow {WorkflowId} could not be loaded", workflowId);
            ErrorMessage = "The workflow status is temporarily unavailable.";
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogError(exception, "Workflow {WorkflowId} status request timed out", workflowId);
            ErrorMessage = "The workflow status request timed out.";
        }
    }

    private static async Task<string> ReadFailureAsync(
        HttpResponseMessage response,
        string fallback)
    {
        try
        {
            var problem = await response.Content.ReadFromJsonAsync<ProblemResponse>();
            return string.IsNullOrWhiteSpace(problem?.Title) ? fallback : problem.Title;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private async Task<string?> UploadEvidenceAsync(
        Guid workflowId,
        IReadOnlyList<IFormFile> files)
    {
        if (files.Count > 5)
        {
            return "Select no more than five evidence files.";
        }

        using var form = new MultipartFormDataContent();
        foreach (var file in files)
        {
            if (file.Length > 10 * 1024 * 1024)
            {
                return $"{file.FileName} is larger than 10 MB.";
            }

            var content = new StreamContent(file.OpenReadStream());
            content.Headers.ContentType = new(file.ContentType);
            form.Add(content, "files", file.FileName);
        }

        var response = await _httpClient.PostAsync(
            $"/api/v1/workflows/{workflowId}/evidence",
            form);
        return response.IsSuccessStatusCode
            ? null
            : await ReadFailureAsync(response, "The evidence files could not be uploaded.");
    }

    public sealed class InputModel
    {
        [BindProperty]
        public string? UserMessage { get; set; }

        [BindProperty]
        public string? DemoScenario { get; set; }

        [BindProperty]
        public List<IFormFile>? EvidenceFiles { get; set; }
    }

    public sealed class ApprovalInputModel
    {
        [BindProperty]
        public string Decision { get; set; } = "approve";

        [BindProperty]
        public string Reason { get; set; } = string.Empty;
    }

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

    public sealed record WorkflowEvidenceResponse(
        Guid Id,
        string FileName,
        string ContentType,
        long Length,
        string Sha256,
        DateTimeOffset UploadedAt);

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
        SupportCaseResponse? SupportCase,
        IReadOnlyList<WorkflowEvidenceResponse> Evidence);

    private sealed record ProblemResponse(string? Title);
}
