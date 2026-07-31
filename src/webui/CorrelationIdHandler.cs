using System.Diagnostics;

namespace BankingAgent.WebUi;

public sealed class CorrelationIdHandler(IHttpContextAccessor httpContextAccessor)
    : DelegatingHandler
{
    protected override Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext;
        var correlationId = context?.Items["CorrelationId"]?.ToString();
        if (!string.IsNullOrWhiteSpace(correlationId))
        {
            request.Headers.TryAddWithoutValidation("x-correlation-id", correlationId);
        }

        return base.SendAsync(request, cancellationToken);
    }
}

public sealed class WebUiCorrelationMiddleware(RequestDelegate next)
{
    private const string HeaderName = "x-correlation-id";

    public async Task InvokeAsync(HttpContext context)
    {
        var requestedCorrelationId = context.Request.Headers[HeaderName].ToString();
        var correlationId =
            !string.IsNullOrWhiteSpace(requestedCorrelationId) &&
            requestedCorrelationId.Length <= 128
                ? requestedCorrelationId
                : context.TraceIdentifier;

        context.Items["CorrelationId"] = correlationId;
        context.Response.Headers[HeaderName] = correlationId;
        Activity.Current?.SetTag("correlation.id", correlationId);

        await next(context);
    }
}
