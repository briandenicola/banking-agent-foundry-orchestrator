using System.Net.Http.Json;
using System.Text.Json;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace webui.Pages;

/// <summary>
/// A window onto the Foundry <c>customer-profile</c> prompt agent: talk to it,
/// and see what its memory store retained as a result. Everything shown under
/// "Retained memories" is read from the memory tool's own output rather than
/// from the model's prose.
/// </summary>
public class ProfileModel : PageModel
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<ProfileModel> _logger;

    public ProfileModel(IHttpClientFactory httpClientFactory, ILogger<ProfileModel> logger)
    {
        _httpClient = httpClientFactory.CreateClient("orchestrator");
        _logger = logger;
    }

    /// <summary>
    /// Prompts that set up and then prove the demonstration. Act 2 is only
    /// meaningful because each turn is a separate request with no history.
    /// </summary>
    public static IReadOnlyList<ProfilePrompt> Prompts { get; } =
    [
        new("Act 1 — State a preference",
            "The customer mentions how they want to be contacted.",
            "Expect: memory written",
            "Please contact me by SMS only, never phone. I also need large-print statements because I have low vision."),
        new("Act 2 — Recall it",
            "A new request with no history. It has to go and look.",
            "Expect: memory recalled",
            "How should you contact me, and is there anything I need for readability?"),
        new("Act 3 — Offer PII",
            "Sensitive details are volunteered alongside a real preference.",
            "Expect: PII excluded",
            "My card number is 4111 1111 1111 1111, my balance is 8,412.66 dollars, and my date of birth is 3 March 1979. Please prefer email for marketing."),
        new("Act 4 — Use a tool",
            "A calculation past what a model does reliably in its head.",
            "Expect: code interpreter",
            "What do you remember about me? Also, my card spends this month were 48.20, 12.99, 130.00, 7.45, 62.10, 19.99, 245.50, 33.25, 8.80, 91.40, 15.60, 74.05, 22.15, 180.30 and 5.99. Work out the total, the mean, and the sample standard deviation, and flag anything more than two standard deviations above the mean.")
    ];

    public async Task<IActionResult> OnPostAskAsync([FromBody] AskRequest request)
    {
        if (string.IsNullOrWhiteSpace(request?.Message))
        {
            return Problem("Enter a message.", 400);
        }

        return await ForwardAsync(
            () => _httpClient.PostAsJsonAsync(
                "/api/v1/profile/messages",
                new { message = request.Message }),
            "The profile agent could not be reached.");
    }

    public Task<IActionResult> OnGetMemoriesAsync() => ForwardAsync(
        () => _httpClient.GetAsync("/api/v1/profile/memories"),
        "The memory store could not be read.");

    public Task<IActionResult> OnPostClearAsync() => ForwardAsync(
        () => _httpClient.DeleteAsync("/api/v1/profile/memories"),
        "The memory store could not be cleared.");

    private async Task<IActionResult> ForwardAsync(
        Func<Task<HttpResponseMessage>> send,
        string unavailableMessage)
    {
        try
        {
            using var response = await send();
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                return Problem(
                    ReadTitle(body, unavailableMessage),
                    (int)response.StatusCode);
            }

            return string.IsNullOrWhiteSpace(body)
                ? new JsonResult(new { cleared = true })
                : Content(body, "application/json");
        }
        catch (HttpRequestException exception)
        {
            _logger.LogError(exception, "The profile agent request failed");
            return Problem(unavailableMessage, 503);
        }
        catch (TaskCanceledException exception)
        {
            _logger.LogError(exception, "The profile agent request timed out");
            return Problem("The profile agent took too long to respond.", 504);
        }
    }

    private static string ReadTitle(string body, string fallback)
    {
        try
        {
            using var document = JsonDocument.Parse(body);
            return document.RootElement.TryGetProperty("title", out var title)
                ? title.GetString() ?? fallback
                : fallback;
        }
        catch (JsonException)
        {
            return fallback;
        }
    }

    private static JsonResult Problem(string error, int status) =>
        new(new { error }) { StatusCode = status };

    public sealed record AskRequest(string Message);

    public sealed record ProfilePrompt(
        string Label,
        string Description,
        string Expectation,
        string Text);
}
