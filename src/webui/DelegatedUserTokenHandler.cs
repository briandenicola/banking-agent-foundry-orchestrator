using System.Net.Http.Headers;
using Microsoft.Identity.Web;

namespace BankingAgent.WebUi;

/// <summary>
/// Attaches a token that identifies the *signed-in customer* to orchestrator
/// calls, so the orchestrator can verify who a request is for instead of taking
/// the Web UI's word for it.
///
/// Why this is not on-behalf-of
/// ----------------------------
/// An earlier design had Container Apps built-in authentication sign the user in
/// and the Web UI exchange the resulting token via <c>AcquireTokenOnBehalfOf</c>.
/// That needed the Easy Auth token store, which is backed by blob storage and a
/// SAS, and the target subscription forbids both shared-key storage
/// authentication and public network access on storage accounts. It cannot work
/// there. See ADR 0005.
///
/// With the Web UI running its own OpenID Connect sign-in it *is* the
/// confidential client, so it already holds a refresh token for this user and
/// can ask Entra for an orchestrator-audience token directly. On-behalf-of
/// exists for a middle tier that receives a token it did not request; that is
/// not this. The resulting token is identical in every property that matters:
/// issued to the Web UI, for this user, with the orchestrator as its audience.
///
/// Distinct from <see cref="OrchestratorTokenHandler"/>, which proves the *Web
/// UI* is calling using its managed identity and says nothing about the person.
///
/// Only the interactive path can work this way. Workflows resume through the
/// recovery worker long after this token has expired, so the background path
/// keeps asserting the customer recorded on the workflow; carrying a user token
/// into it would mean persisting user credentials in the workflow store.
/// </summary>
public sealed class DelegatedUserTokenHandler(
    IHttpContextAccessor httpContextAccessor,
    string scope) : DelegatingHandler
{
    protected override async Task<HttpResponseMessage> SendAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        var context = httpContextAccessor.HttpContext
            ?? throw new InvalidOperationException(
                "Delegated user authentication requires an HTTP context. The orchestrator client cannot be "
                + "used from a background task under this configuration, because there is no signed-in user "
                + "to acquire a token for.");

        // Resolved per request rather than injected. ITokenAcquisition is
        // scoped, and HttpMessageHandler instances are pooled and reused across
        // requests, so a constructor-injected copy would be captured from
        // whichever scope happened to build the handler and would outlive it.
        var tokenAcquisition = context.RequestServices.GetRequiredService<ITokenAcquisition>();

        var token = await tokenAcquisition.GetAccessTokenForUserAsync([scope], user: context.User);

        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        return await base.SendAsync(request, cancellationToken);
    }
}
