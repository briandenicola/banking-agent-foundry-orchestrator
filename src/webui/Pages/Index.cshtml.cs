using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace webui.Pages;

public class IndexModel : PageModel
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<IndexModel> _logger;

    public IndexModel(IHttpClientFactory httpClientFactory, ILogger<IndexModel> logger)
    {
        _httpClient = httpClientFactory.CreateClient("orchestrator");
        _logger = logger;
    }

    [BindProperty]
    public InputModel Input { get; set; } = new();

    [BindProperty]
    public ApprovalInputModel ApprovalInput { get; set; } = new();

    [TempData]
    public string? StatusMessage { get; set; }

    [TempData]
    public string? ErrorMessage { get; set; }

    public WorkflowDetailResponse? Workflow { get; private set; }

    public async Task OnGetAsync(Guid? workflowId)
    {
        if (workflowId.HasValue)
        {
            await LoadWorkflowAsync(workflowId.Value);
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
            var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/workflows",
                new { userMessage = Input.UserMessage });
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

            StatusMessage = "Workflow submitted successfully.";
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

    public sealed class InputModel
    {
        [BindProperty]
        public string? UserMessage { get; set; }
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
        SupportCaseResponse? SupportCase);

    private sealed record ProblemResponse(string? Title);
}
