using System.Net;
using System.Net.Http.Json;
using Microsoft.AspNetCore.Antiforgery;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BankingAgent.WebUi.Tests;

/// <summary>
/// Real HTTP integration tests using WebApplicationFactory.
/// These tests send an ACTUAL HTTP POST via HttpClient through the full
/// ASP.NET Core middleware pipeline, proving the no-evidence submission path
/// works end-to-end at the HTTP boundary (not just PageModel unit tests).
///
/// Aria review requirement (aria-evidence-final-review.md §4):
///   "The smallest adequate mechanism: WebApplicationFactory + HttpClient posting
///    a multipart/form-data body with Input.UserMessage set and Input.EvidenceFiles
///    absent, asserting a redirect (302) response."
/// </summary>
public sealed class NoEvidencePostIntegrationTests : IClassFixture<NoEvidencePostIntegrationTests.Factory>
{
    private readonly Factory _factory;

    public NoEvidencePostIntegrationTests(Factory factory) => _factory = factory;

    /// <summary>
    /// HTTP POST with UserMessage set and no EvidenceFiles must result in a 302 redirect
    /// to the workflow status page.  This is the authoritative proof that a real HTTP POST
    /// with no evidence does NOT error and does NOT stay on the same page.
    /// </summary>
    [Fact]
    public async Task RealHttpPost_NoEvidenceWithUserMessage_Redirects302ToWorkflowPage()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false  // inspect 302 directly
        });

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent("Dispute charge on 2026-07-15"), "Input.UserMessage");
        content.Add(new StringContent(""), "Input.DemoScenario");

        var response = await client.PostAsync("/", content);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
        var location = response.Headers.Location?.ToString() ?? "";
        Assert.True(
            location.Contains("workflowId", StringComparison.OrdinalIgnoreCase),
            $"Redirect location must contain workflowId. Actual: {location}");
    }

    /// <summary>
    /// Same as above but via application/x-www-form-urlencoded — both encodings must work
    /// since the actual HTML form uses enctype=multipart/form-data but the model binder
    /// accepts both.
    /// </summary>
    [Fact]
    public async Task RealHttpPost_FormUrlEncoded_NoEvidenceWithUserMessage_Redirects302()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        var form = new FormUrlEncodedContent(new[]
        {
            new KeyValuePair<string, string>("Input.UserMessage", "Balance inquiry no-evidence"),
            new KeyValuePair<string, string>("Input.DemoScenario", ""),
        });

        var response = await client.PostAsync("/", form);

        Assert.Equal(HttpStatusCode.Redirect, response.StatusCode);
    }

    /// <summary>
    /// POST without UserMessage must NOT redirect — it must return a page response
    /// with the validation error (no NullReferenceException, no 500).
    /// </summary>
    [Fact]
    public async Task RealHttpPost_EmptyUserMessage_Returns200WithPageAndNoException()
    {
        var client = _factory.CreateClient(new WebApplicationFactoryClientOptions
        {
            AllowAutoRedirect = false
        });

        using var content = new MultipartFormDataContent();
        content.Add(new StringContent(""), "Input.UserMessage");

        var response = await client.PostAsync("/", content);

        // 200 = form re-rendered with error, not 302 redirect, not 500.
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var body = await response.Content.ReadAsStringAsync();
        Assert.Contains("Enter a banking request", body,
            StringComparison.OrdinalIgnoreCase);
    }

    // ────────────────────────────────────────────────────────────────────────
    // Test factory
    // ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// WebApplicationFactory that wires the WebUI application for in-process
    /// HTTP testing without live Azure dependencies.
    ///
    /// Overrides:
    ///   1. IAntiforgery → no-op so tests can POST without extracting tokens.
    ///   2. "orchestrator" named HttpClient primary handler → returns a fake 202 response.
    ///   3. Configuration for ORCHESTRATOR_API_BASE_URL (no real service needed).
    /// </summary>
    public sealed class Factory : WebApplicationFactory<Program>
    {
        protected override void ConfigureWebHost(IWebHostBuilder builder)
        {
            // Point at a dummy orchestrator base URL; the handler below intercepts all calls.
            builder.UseSetting("ORCHESTRATOR_API_BASE_URL", "http://fake-orchestrator");

            builder.ConfigureTestServices(services =>
            {
                // Replace antiforgery with a no-op so integration tests can POST without tokens.
                services.Replace(ServiceDescriptor.Singleton<IAntiforgery, TestNoOpAntiforgery>());

                // Intercept all outbound calls to the orchestrator named client.
                services.PostConfigure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
                    "orchestrator",
                    options =>
                    {
                        options.HttpMessageHandlerBuilderActions.Clear();
                        options.HttpMessageHandlerBuilderActions.Add(b =>
                            b.PrimaryHandler = new FakeOrchestratorMessageHandler());
                    });

                // Same for the health-check client so startup doesn't fail.
                services.PostConfigure<Microsoft.Extensions.Http.HttpClientFactoryOptions>(
                    "orchestrator-health",
                    options =>
                    {
                        options.HttpMessageHandlerBuilderActions.Clear();
                        options.HttpMessageHandlerBuilderActions.Add(b =>
                            b.PrimaryHandler = new FakeOrchestratorMessageHandler());
                    });
            });
        }
    }

    /// <summary>
    /// Returns a fake 202 Accepted response for any request, simulating a
    /// healthy orchestrator that accepts new workflows.
    /// </summary>
    private sealed class FakeOrchestratorMessageHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var workflowId = Guid.NewGuid();
            var response = new HttpResponseMessage(HttpStatusCode.Accepted)
            {
                Content = JsonContent.Create(new
                {
                    workflowId,
                    traceId = Guid.NewGuid().ToString("N"),
                    status = "Draft",
                    message = "Workflow accepted for processing."
                })
            };
            return Task.FromResult(response);
        }
    }

    /// <summary>
    /// No-op antiforgery implementation that validates all requests as trusted.
    /// Used exclusively in tests so POST requests do not need to carry CSRF tokens.
    /// </summary>
    private sealed class TestNoOpAntiforgery : IAntiforgery
    {
        public AntiforgeryTokenSet GetAndStoreTokens(HttpContext httpContext) =>
            new("test-token", "test-cookie", "__RequestVerificationToken", "Test");

        public AntiforgeryTokenSet GetTokens(HttpContext httpContext) =>
            new("test-token", "test-cookie", "__RequestVerificationToken", "Test");

        public Task<bool> IsRequestValidAsync(HttpContext httpContext) =>
            Task.FromResult(true);

        public void SetCookieTokenAndHeader(HttpContext httpContext) { }

        public Task ValidateRequestAsync(HttpContext httpContext) =>
            Task.CompletedTask;
    }
}
