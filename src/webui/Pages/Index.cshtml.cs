using System.Net.Http.Json;
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

    public string? StatusMessage { get; private set; }

    public WorkflowResponse? Workflow { get; private set; }

    public void OnGet()
    {
    }

    public async Task<IActionResult> OnPostAsync()
    {
        if (string.IsNullOrWhiteSpace(Input.UserMessage))
        {
            StatusMessage = "Provide a request before submitting the workflow.";
            return Page();
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                "/api/v1/workflows",
                new { userMessage = Input.UserMessage });
            if (!response.IsSuccessStatusCode)
            {
                StatusMessage = "The workflow service rejected the request.";
                return Page();
            }

            Workflow = await response.Content.ReadFromJsonAsync<WorkflowResponse>();
            StatusMessage = "Workflow submitted successfully.";
            return Page();
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "The orchestrator API could not be reached");
            StatusMessage = "The workflow service could not be reached.";
            return Page();
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogError(exception, "The orchestrator API request timed out");
            StatusMessage = "The workflow service timed out.";
            return Page();
        }
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid workflowId)
    {
        if (ApprovalInput.Decision is not ("approve" or "reject")
            || string.IsNullOrWhiteSpace(ApprovalInput.Reason))
        {
            StatusMessage = "Provide an approval decision and reason.";
            return Page();
        }

        if (workflowId == Guid.Empty)
        {
            StatusMessage = "Create a workflow before approving it.";
            return Page();
        }

        try
        {
            var response = await _httpClient.PostAsJsonAsync(
                $"/api/v1/workflows/{workflowId}/approval",
                new { decision = ApprovalInput.Decision, reason = ApprovalInput.Reason });
            if (!response.IsSuccessStatusCode)
            {
                StatusMessage = "The approval request was rejected.";
                return Page();
            }

            Workflow = await response.Content.ReadFromJsonAsync<WorkflowResponse>();
            StatusMessage = "Approval recorded successfully.";
            return Page();
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "The orchestrator API could not be reached for approval");
            StatusMessage = "The approval service could not be reached.";
            return Page();
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogError(exception, "The orchestrator approval request timed out");
            StatusMessage = "The approval service timed out.";
            return Page();
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
}
