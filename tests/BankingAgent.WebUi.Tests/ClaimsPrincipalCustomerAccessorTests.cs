using System.Security.Claims;
using BankingAgent.WebUi;
using Microsoft.AspNetCore.Http;
using Xunit;

namespace BankingAgent.WebUi.Tests;

/// <summary>
/// When the Web UI runs its own OpenID Connect sign-in, the identity comes from
/// a principal this process built from a token it validated itself, rather than
/// from headers a fronting platform injected. These tests pin the two things
/// that would silently break customer isolation if they regressed: an
/// unauthenticated principal must never yield an identity, and the identifier
/// must come from the object identifier claim rather than from any
/// human-readable claim that a user can influence.
/// </summary>
public class ClaimsPrincipalCustomerAccessorTests
{
    private static ClaimsPrincipalCustomerAccessor Build(ClaimsPrincipal? principal)
    {
        var context = new DefaultHttpContext();
        if (principal is not null)
        {
            context.User = principal;
        }

        return new ClaimsPrincipalCustomerAccessor(new HttpContextAccessor { HttpContext = context });
    }

    private static ClaimsPrincipal Authenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(
            claims.Select(claim => new Claim(claim.Type, claim.Value)),
            authenticationType: "TestOidc"));

    private static ClaimsPrincipal Unauthenticated(params (string Type, string Value)[] claims) =>
        new(new ClaimsIdentity(claims.Select(claim => new Claim(claim.Type, claim.Value))));

    [Fact]
    public void An_unauthenticated_principal_carrying_claims_yields_nobody()
    {
        // An identity constructed without an authentication type reports
        // IsAuthenticated=false however many claims it carries. Honouring its
        // claims would let an unvalidated principal name any customer.
        var accessor = Build(Unauthenticated(
            ("oid", "11111111-1111-1111-1111-111111111111"),
            ("preferred_username", "attacker@contoso.com")));

        Assert.False(accessor.Current.IsAuthenticated);
        Assert.Equal(string.Empty, accessor.Current.Id);
    }

    [Fact]
    public void No_http_context_yields_nobody()
    {
        var accessor = new ClaimsPrincipalCustomerAccessor(new HttpContextAccessor { HttpContext = null });

        Assert.False(accessor.Current.IsAuthenticated);
    }

    [Fact]
    public void An_authenticated_principal_without_an_object_identifier_yields_nobody()
    {
        // A display name is not an identity. Falling back to one would key the
        // remembered profile on a value the directory allows to be duplicated
        // and changed.
        var accessor = Build(Authenticated(
            ("name", "Brian Denicola"),
            ("preferred_username", "brian@contoso.com")));

        Assert.False(accessor.Current.IsAuthenticated);
    }

    [Fact]
    public void The_identifier_comes_from_the_short_oid_claim()
    {
        var accessor = Build(Authenticated(
            ("oid", "22222222-2222-2222-2222-222222222222"),
            ("preferred_username", "brian@contoso.com"),
            ("given_name", "Brian")));

        var current = accessor.Current;

        Assert.True(current.IsAuthenticated);
        Assert.Equal("22222222-2222-2222-2222-222222222222", current.Id);
        Assert.Equal("brian@contoso.com", current.DisplayName);
        Assert.Equal("Brian", current.GivenName);
    }

    [Fact]
    public void The_schema_form_of_the_object_identifier_is_accepted()
    {
        // Present instead of "oid" when inbound claim mapping is left on, which
        // it is by default for handlers other than the one this application
        // configures.
        var accessor = Build(Authenticated(
            ("http://schemas.microsoft.com/identity/claims/objectidentifier",
                "33333333-3333-3333-3333-333333333333")));

        Assert.Equal("33333333-3333-3333-3333-333333333333", accessor.Current.Id);
    }

    [Fact]
    public void A_given_name_is_taken_from_the_schema_claim_when_the_short_one_is_absent()
    {
        var accessor = Build(Authenticated(
            ("oid", "44444444-4444-4444-4444-444444444444"),
            (ClaimTypes.GivenName, "Brian")));

        Assert.Equal("Brian", accessor.Current.GivenName);
    }

    [Fact]
    public void A_display_name_that_is_an_address_is_not_used_as_a_greeting()
    {
        // "Hello brian@contoso.com" reads as a bug, so no greeting is better.
        var accessor = Build(Authenticated(
            ("oid", "55555555-5555-5555-5555-555555555555"),
            ("name", "brian@contoso.com")));

        Assert.Null(accessor.Current.GivenName);
    }

    [Fact]
    public void A_first_word_of_the_display_name_is_used_when_no_given_name_claim_exists()
    {
        var accessor = Build(Authenticated(
            ("oid", "66666666-6666-6666-6666-666666666666"),
            ("name", "Brian Denicola")));

        Assert.Equal("Brian", accessor.Current.GivenName);
    }
}
