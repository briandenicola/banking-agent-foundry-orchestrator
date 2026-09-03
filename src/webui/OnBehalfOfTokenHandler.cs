using Microsoft.Identity.Client;

namespace BankingAgent.WebUi;

/// <summary>
/// Exchanges the signed-in user's token for one addressed to the orchestrator,
/// so the orchestrator can verify which customer a request is for instead of
/// taking the Web UI's word for it.
///
/// This is the on-behalf-of flow proper: the Web UI holds a token whose audience
/// is itself, and trades it for one whose audience is the orchestrator. It is
/// distinct from <see cref="OrchestratorTokenHandler"/>, which proves the *Web
/// UI* is calling using the managed identity and says nothing about the person.
///
/// Only the interactive path can work this way. Workflows resume through the
/// recovery worker long after this token has expired, so the background path
/// keeps using the managed identity; carrying a user token into it would mean
/// persisting user credentials in the workflow store.
/// </summary>
public sealed class OnBehalfOfTokenHandler(
    IConfidentialClientApplication client,
    IHttpContextAccessor httpContextAccessor,
    string scope,
    ILogger<OnBehalfOfTokenHandler> logger) : DelegatingHandler
{
    /// <summary>
    /// Injected by Easy Auth, but only when the token store is enabled. Without
    /// the store the platform still authenticates the user and still sets the
    /// principal headers, so sign-in looks entirely healthy while this header is
    /// silently absent.
    /// </summary>
    private const string AccessTokenHeader = "X-MS-TOKEN-AAD-ACCESS-TOKEN";

    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var userToken = httpContextAccessor.HttpContext?.Request.Headers[AccessTokenHeader].ToString();

        if (string.IsNullOrWhiteSpace(userToken))
        {
            // Deliberately fatal rather than falling through unauthenticated.
            // A request sent without the exchanged token would be refused by the
            // orchestrator as a plain 401, which looks like a sign-in problem
            // and sends anyone debugging it towards the wrong half of the system.
            throw new InvalidOperationException(
                $"On-behalf-of is enabled but the request carried no {AccessTokenHeader} header. "
                + "Easy Auth only injects it when the token store is enabled, so check "
                + "login.tokenStore on the Web UI's auth configuration.");
        }

        AuthenticationResult result;
        try
        {
            result = await client
                .AcquireTokenOnBehalfOf([scope], new UserAssertion(userToken))
                .ExecuteAsync(cancellationToken);
        }
        catch (MsalUiRequiredException exception)
        {
            // Raised when the user has not consented to the orchestrator's
            // scope. Pre-authorizing the Web UI on the orchestrator's app
            // registration is what avoids it.
            throw new InvalidOperationException(
                "The signed-in user has not consented to the orchestrator scope. Add the Web UI to "
                + "the orchestrator registration's pre-authorized applications, or grant admin consent.",
                exception);
        }

        logger.LogDebug("Exchanged the user token for an orchestrator token expiring at {ExpiresOn}.", result.ExpiresOn);
        request.Headers.Authorization = new System.Net.Http.Headers.AuthenticationHeaderValue("Bearer", result.AccessToken);
        return await base.SendAsync(request, cancellationToken);
    }
}
