using Azure.Core;
using System.Net.Http.Headers;

/// <summary>
/// Attaches a managed-identity bearer token scoped to the orchestrator API on every
/// outbound request made by the named "orchestrator" HttpClient.
/// </summary>
internal sealed class OrchestratorTokenHandler(TokenCredential credential, string tokenScope)
    : DelegatingHandler
{
    private readonly TokenRequestContext _requestContext = new([tokenScope]);

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request, CancellationToken cancellationToken)
    {
        var token = await credential.GetTokenAsync(_requestContext, cancellationToken);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token.Token);
        return await base.SendAsync(request, cancellationToken);
    }
}
