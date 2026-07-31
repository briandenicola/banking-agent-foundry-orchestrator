using System.Net;
using BankingAgent.Infrastructure;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Xunit;

namespace BankingAgent.Infrastructure.Tests;

public sealed class FoundryMcpClientReliabilityTests
{
    [Fact]
    public async Task InvokeAsync_TransientResponses_RetriesUntilSuccess()
    {
        var handler = new SequenceHandler(
            _ => Task.FromResult(Response(HttpStatusCode.ServiceUnavailable)),
            _ => Task.FromResult(Response(HttpStatusCode.TooManyRequests)),
            _ => Task.FromResult(Response(HttpStatusCode.OK, """{"status":"ok"}""")));
        var client = CreateClient(handler, maxAttempts: 3);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("ok", result.Status);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal(3, result.Data["attempts"]);
    }

    [Fact]
    public async Task InvokeAsync_NonTransientResponse_DoesNotRetry()
    {
        var handler = new SequenceHandler(
            _ => Task.FromResult(Response(HttpStatusCode.BadRequest)));
        var client = CreateClient(handler, maxAttempts: 3);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal(1, handler.Attempts);
        Assert.Equal(1, result.Data["attempts"]);
    }

    [Fact]
    public async Task InvokeAsync_TransportFailure_StopsAtBoundedAttemptCount()
    {
        var handler = new SequenceHandler(
            _ => throw new HttpRequestException("synthetic transport failure"),
            _ => throw new HttpRequestException("synthetic transport failure"),
            _ => throw new HttpRequestException("synthetic transport failure"));
        var client = CreateClient(handler, maxAttempts: 3);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal(3, handler.Attempts);
        Assert.Equal("transport_error", result.Data["error_code"]);
        Assert.Equal(3, result.Data["attempts"]);
        Assert.DoesNotContain(
            "synthetic transport failure",
            string.Join(" ", result.Data.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task InvokeAsync_AttemptTimeout_StopsAtBoundedAttemptCount()
    {
        var handler = new SequenceHandler(
            token => Task.FromCanceled<HttpResponseMessage>(
                new CancellationToken(canceled: true)),
            token => Task.FromCanceled<HttpResponseMessage>(
                new CancellationToken(canceled: true)));
        var client = CreateClient(handler, maxAttempts: 2);

        var result = await client.InvokeAsync("workflow.plan", Parameters());

        Assert.Equal("error", result.Status);
        Assert.Equal(2, handler.Attempts);
        Assert.Equal("timeout", result.Data["error_code"]);
    }

    [Fact]
    public async Task InvokeAsync_CallerCancellation_IsNeverRetried()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var handler = new SequenceHandler(
            token => Task.FromCanceled<HttpResponseMessage>(token));
        var client = CreateClient(handler, maxAttempts: 3);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => client.InvokeAsync(
                "workflow.plan",
                Parameters(),
                cancellation.Token));

        Assert.Equal(1, handler.Attempts);
    }

    private static FoundryMcpClient CreateClient(
        HttpMessageHandler handler,
        int maxAttempts) =>
        new(
            new HttpClient(handler),
            NullLogger<FoundryMcpClient>.Instance,
            Options.Create(new FoundryMcpClientOptions
            {
                DefaultEndpoint = "https://example.test/agent",
                MaxAttempts = maxAttempts,
                AttemptTimeoutSeconds = 1,
                BaseDelayMilliseconds = 0
            }));

    private static Dictionary<string, object?> Parameters() =>
        new()
        {
            ["workflow_id"] = Guid.NewGuid().ToString(),
            ["trace_id"] = Guid.NewGuid().ToString("N"),
            ["user_message"] = "Synthetic request"
        };

    private static HttpResponseMessage Response(
        HttpStatusCode statusCode,
        string body = """{"error":{"code":"synthetic"}}""") =>
        new(statusCode)
        {
            Content = new StringContent(body)
        };

    private sealed class SequenceHandler(
        params Func<CancellationToken, Task<HttpResponseMessage>>[] attempts)
        : HttpMessageHandler
    {
        private int _attempts;

        public int Attempts => _attempts;

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var attempt = Interlocked.Increment(ref _attempts);
            return attempts[Math.Min(attempt, attempts.Length) - 1](cancellationToken);
        }
    }
}
