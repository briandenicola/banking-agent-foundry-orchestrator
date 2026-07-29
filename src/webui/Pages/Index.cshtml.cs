using System.ComponentModel.DataAnnotations;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace webui.Pages;

public class IndexModel : PageModel
{
    private readonly HttpClient _httpClient;

    public IndexModel(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
        var response = await _httpClient.PostAsJsonAsync("http://localhost:5000/api/v1/workflows", new { userMessage = Input.UserMessage });
        if (!response.IsSuccessStatusCode)
        {
            StatusMessage = "The workflow service could not be reached.";
            return Page();
        }

        Workflow = await response.Content.ReadFromJsonAsync<WorkflowResponse>();
        StatusMessage = "Workflow submitted successfully.";
        return Page();
    }

    public async Task<IActionResult> OnPostApproveAsync(Guid workflowId)
    {
        if (workflowId == Guid.Empty)
        {
            StatusMessage = "Create a workflow before approving it.";
            return Page();
        }

        var response = await _httpClient.PostAsJsonAsync($"http://localhost:5000/api/v1/workflows/{workflowId}/approval", new { decision = ApprovalInput.Decision, reason = ApprovalInput.Reason });
        if (!response.IsSuccessStatusCode)
        {
            StatusMessage = "The approval request could not be submitted.";
            return Page();
        }

        Workflow = await response.Content.ReadFromJsonAsync<WorkflowResponse>();
        StatusMessage = "Approval recorded successfully.";
        return Page();
    }

    public sealed class InputModel
    {
        [BindProperty]
        [Required]
        public string? UserMessage { get; set; }
    }

    public sealed class ApprovalInputModel
    {
        [BindProperty]
        [Required]
        public string Decision { get; set; } = "approve";

        [BindProperty]
        [Required]
        public string Reason { get; set; } = string.Empty;
    }

    public sealed record WorkflowResponse(Guid WorkflowId, string TraceId, string Status, string Message);
}
