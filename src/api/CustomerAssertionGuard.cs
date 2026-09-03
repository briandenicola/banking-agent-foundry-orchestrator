using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace BankingAgent.Api;

/// <summary>
/// Checks that a request asking to act for a customer is carrying that
/// customer's own token.
///
/// The orchestrator has always taken the customer identifier as a value in the
/// request and asserted it onward as a memory scope. That is safe only while the
/// orchestrator has internal ingress and the Web UI is its sole caller: the
/// customer is trusted because of where the request came from, not because of
/// anything in the request itself. Anything that could reach the orchestrator
/// directly could read any customer's remembered profile by naming them.
///
/// With an on-behalf-of token the request carries the customer's own identity,
/// so the claim can be verified rather than trusted. A mismatch is refused
/// outright rather than quietly corrected to the token's subject, because the
/// only things that produce one are a bug in the caller or an attempt to read
/// somebody else's profile, and neither should be silently absorbed.
/// </summary>
public interface ICustomerAssertionGuard
{
    /// <summary>
    /// Returns a problem result when the asserted customer contradicts the
    /// caller's token, or null when the request may proceed.
    /// </summary>
    IResult? Validate(string? assertedCustomerId);
}

public sealed class CustomerAssertionGuard(
    IHttpContextAccessor httpContextAccessor,
    bool enabled) : ICustomerAssertionGuard
{
    /// <summary>
    /// The Entra object ID of the signed-in user. Claim mapping must be off for
    /// this to be present under its short name.
    /// </summary>
    private const string ObjectIdClaim = "oid";

    public IResult? Validate(string? assertedCustomerId)
    {
        if (!enabled)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(assertedCustomerId))
        {
            // No claim was made, so there is nothing to contradict. Such a
            // request reads the scope derived from the orchestrator's own
            // identity, which holds no customer's data.
            return null;
        }

        var principal = httpContextAccessor.HttpContext?.User;
        var subject = ReadObjectId(principal);

        if (string.IsNullOrWhiteSpace(subject))
        {
            return Results.Problem(
                title: "The request did not carry a user identity.",
                detail: "On-behalf-of authentication is enabled, so acting for a customer requires a delegated user token.",
                statusCode: StatusCodes.Status401Unauthorized);
        }

        if (!string.Equals(subject, assertedCustomerId, StringComparison.OrdinalIgnoreCase))
        {
            return Results.Problem(
                title: "The request asked to act for a different customer than the token identifies.",
                statusCode: StatusCodes.Status403Forbidden);
        }

        return null;
    }

    private static string? ReadObjectId(ClaimsPrincipal? principal) =>
        principal?.FindFirst(ObjectIdClaim)?.Value
        // Present when inbound claim mapping is on. Read as a fallback so a
        // configuration change elsewhere degrades to working rather than to
        // rejecting every customer.
        ?? principal?.FindFirst("http://schemas.microsoft.com/identity/claims/objectidentifier")?.Value;
}

/// <summary>
/// Used when on-behalf-of authentication is switched off, which is the default
/// and the behaviour every existing deployment has.
/// </summary>
public sealed class DisabledCustomerAssertionGuard : ICustomerAssertionGuard
{
    public IResult? Validate(string? assertedCustomerId) => null;
}
