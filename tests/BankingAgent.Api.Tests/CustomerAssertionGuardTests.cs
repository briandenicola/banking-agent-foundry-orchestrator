using System.Security.Claims;
using BankingAgent.Api;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BankingAgent.Api.Tests;

public sealed class CustomerAssertionGuardTests
{
    private const string ObjectId = "98abb71d-84a4-4e7d-ba3b-db375e15a10e";

    [Fact]
    public void RequestForTheTokensOwnCustomerIsAllowed()
    {
        var guard = CreateGuard(ObjectId);

        Assert.Null(guard.Validate(ObjectId));
    }

    [Fact]
    public void RequestForADifferentCustomerIsRefused()
    {
        var guard = CreateGuard(ObjectId);

        var result = guard.Validate("cd3cbaf1-9b7a-4b1a-9e19-a13a630b88fe");

        Assert.Equal(StatusCodes.Status403Forbidden, StatusCodeOf(result));
    }

    [Fact]
    public void RequestWithoutAUserIdentityIsRefused()
    {
        var guard = CreateGuard(objectId: null);

        var result = guard.Validate(ObjectId);

        Assert.Equal(StatusCodes.Status401Unauthorized, StatusCodeOf(result));
    }

    [Fact]
    public void RequestThatNamesNoCustomerIsAllowedWithoutAnIdentity()
    {
        // Nothing is being asserted, so there is nothing to contradict. Such a
        // request cannot reach another customer's data.
        var guard = CreateGuard(objectId: null);

        Assert.Null(guard.Validate(null));
        Assert.Null(guard.Validate("   "));
    }

    [Fact]
    public void ObjectIdComparisonIgnoresCase()
    {
        // Entra renders GUIDs in lowercase but callers may not, and a case
        // difference is not a different customer.
        var guard = CreateGuard(ObjectId.ToUpperInvariant());

        Assert.Null(guard.Validate(ObjectId));
    }

    [Fact]
    public void MappedObjectIdClaimIsAccepted()
    {
        var guard = CreateGuard(
            ObjectId,
            claimType: "http://schemas.microsoft.com/identity/claims/objectidentifier");

        Assert.Null(guard.Validate(ObjectId));
    }

    [Fact]
    public void DisabledGuardAllowsAnyCustomer()
    {
        // The default, and the behaviour every deployment without on-behalf-of
        // has: the caller's assertion is trusted because of where it came from.
        var guard = new CustomerAssertionGuard(
            CreateAccessor(objectId: null, claimType: "oid"),
            enabled: false);

        Assert.Null(guard.Validate("someone-else"));
        Assert.Null(new DisabledCustomerAssertionGuard().Validate("someone-else"));
    }

    private static CustomerAssertionGuard CreateGuard(string? objectId, string claimType = "oid") =>
        new(CreateAccessor(objectId, claimType), enabled: true);

    private static IHttpContextAccessor CreateAccessor(string? objectId, string claimType)
    {
        var identity = objectId is null
            ? new ClaimsIdentity()
            : new ClaimsIdentity([new Claim(claimType, objectId)], "Bearer");

        return new HttpContextAccessor
        {
            HttpContext = new DefaultHttpContext { User = new ClaimsPrincipal(identity) },
        };
    }

    private static int StatusCodeOf(IResult? result)
    {
        var problem = Assert.IsAssignableFrom<IStatusCodeHttpResult>(result);
        return problem.StatusCode ?? 0;
    }
}
