using System.Diagnostics;
using Microsoft.Extensions.Primitives;

namespace BankingAgent.Orchestrator;

public sealed class CorrelationIdMiddleware
{
    private const string HeaderName = "x-correlation-id";
    private readonly RequestDelegate _next;
    private readonly ILogger<CorrelationIdMiddleware> _logger;

    public CorrelationIdMiddleware(RequestDelegate next, ILogger<CorrelationIdMiddleware> logger)
    {
        _next = next;
        _logger = logger;
    }

    public async Task InvokeAsync(HttpContext context)
    {
        var requestedCorrelationId =
            context.Request.Headers.TryGetValue(HeaderName, out StringValues headerValue)
                ? headerValue.ToString()
                : null;
        var correlationId =
            !string.IsNullOrWhiteSpace(requestedCorrelationId) &&
            requestedCorrelationId.Length <= 128
                ? requestedCorrelationId
                : Guid.NewGuid().ToString("N");

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation_id", correlationId);
        Activity.Current?.SetTag("correlation.id", correlationId);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["CorrelationId"] = correlationId
        });

        await _next(context);
    }
}
