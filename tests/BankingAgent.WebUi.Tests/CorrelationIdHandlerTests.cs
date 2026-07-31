using BankingAgent.WebUi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BankingAgent.WebUi.Tests;

public sealed class CorrelationIdHandlerTests
{
    [Fact]
    public async Task SendAsync_ForwardsCurrentRequestCorrelationId()
    {
        var httpContext = new DefaultHttpContext();
        httpContext.Items["CorrelationId"] = "correlation-test-123";
        var captureHandler = new CaptureHandler();
        var handler = new CorrelationIdHandler(new HttpContextAccessor
        {
            HttpContext = httpContext
        })
        {
            InnerHandler = captureHandler
        };
        using var client = new HttpClient(handler);

        await client.GetAsync("https://orchestrator.example/api/v1/workflows");

        Assert.Equal(
            "correlation-test-123",
            captureHandler.Request?.Headers.GetValues("x-correlation-id").Single());
    }

    private sealed class CaptureHandler : HttpMessageHandler
    {
        public HttpRequestMessage? Request { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK));
        }
    }
}
