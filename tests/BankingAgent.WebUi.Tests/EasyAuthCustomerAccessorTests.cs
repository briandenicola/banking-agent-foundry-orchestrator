using System.Text;
using BankingAgent.WebUi;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Xunit;

namespace BankingAgent.WebUi.Tests;

/// <summary>
/// The Easy Auth principal headers are only trustworthy while the platform is
/// terminating authentication in front of the container, because it is the
/// platform that strips any client-supplied copy. With authentication off the
/// container is directly reachable and those headers become caller-controlled,
/// so honouring them would let anyone assert any identity and have a workflow
/// personalised with that customer's remembered profile.
///
/// The first test is the important one: it forges the exact headers an attacker
/// would send and requires that they are ignored.
/// </summary>
public class EasyAuthCustomerAccessorTests
{
    private static EasyAuthCustomerAccessor Build(bool authEnabled, params (string Name, string Value)[] headers)
    {
        var context = new DefaultHttpContext();
        foreach (var (name, value) in headers)
        {
            context.Request.Headers[name] = value;
        }

        var accessor = new HttpContextAccessor { HttpContext = context };
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["WEBUI_AUTH_ENABLED"] = authEnabled ? "true" : "false"
            })
            .Build();

        return new EasyAuthCustomerAccessor(accessor, configuration);
    }

    [Fact]
    public void Forged_principal_headers_are_ignored_when_authentication_is_disabled()
    {
        var accessor = Build(
            authEnabled: false,
            ("X-MS-CLIENT-PRINCIPAL-ID", "victim-object-id"),
            ("X-MS-CLIENT-PRINCIPAL-NAME", "victim@example.com"));

        Assert.False(accessor.Current.IsAuthenticated);
        Assert.Equal(string.Empty, accessor.Current.Id);
    }

    [Fact]
    public void Principal_headers_are_read_when_the_platform_is_authenticating()
    {
        var accessor = Build(
            authEnabled: true,
            ("X-MS-CLIENT-PRINCIPAL-ID", "  object-id  "),
            ("X-MS-CLIENT-PRINCIPAL-NAME", "person@example.com"));

        Assert.True(accessor.Current.IsAuthenticated);
        Assert.Equal("object-id", accessor.Current.Id);
        Assert.Equal("person@example.com", accessor.Current.DisplayName);
    }

    [Fact]
    public void Missing_headers_yield_no_identity_even_with_authentication_enabled()
    {
        var accessor = Build(authEnabled: true);

        Assert.False(accessor.Current.IsAuthenticated);
    }

    [Fact]
    public void Encoded_principal_supplies_the_object_identifier_when_the_short_header_is_absent()
    {
        var principal = """
        {"claims":[
          {"typ":"http://schemas.microsoft.com/identity/claims/objectidentifier","val":"encoded-oid"},
          {"typ":"preferred_username","val":"person@example.com"}
        ]}
        """;
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal));

        var accessor = Build(authEnabled: true, ("X-MS-CLIENT-PRINCIPAL", encoded));

        Assert.Equal("encoded-oid", accessor.Current.Id);
        Assert.Equal("person@example.com", accessor.Current.DisplayName);
    }

    [Theory]
    [InlineData("not-base64!!")]
    [InlineData("")]
    public void Malformed_encoded_principal_yields_no_identity(string encoded)
    {
        // A request that cannot be attributed with confidence is anonymous
        // rather than guessed at.
        var (id, _) = EasyAuthCustomerAccessor.ReadEncodedPrincipal(encoded);

        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void Encoded_principal_without_an_object_identifier_yields_no_identity()
    {
        var principal = """{"claims":[{"typ":"preferred_username","val":"person@example.com"}]}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal));

        var (id, _) = EasyAuthCustomerAccessor.ReadEncodedPrincipal(encoded);

        Assert.Equal(string.Empty, id);
    }

    [Fact]
    public void Given_name_claim_is_preferred_for_the_greeting()
    {
        var principal = """{"claims":[{"typ":"given_name","val":"Brian"},{"typ":"name","val":"Brian Denicola"}]}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal));

        Assert.Equal("Brian", EasyAuthCustomerAccessor.ReadGivenName(encoded));
    }

    [Fact]
    public void Display_name_falls_back_to_its_first_word()
    {
        var principal = """{"claims":[{"typ":"name","val":"Brian Denicola"}]}""";
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal));

        Assert.Equal("Brian", EasyAuthCustomerAccessor.ReadGivenName(encoded));
    }

    [Theory]
    [InlineData("""{"claims":[{"typ":"name","val":"brdenico@contoso.com"}]}""")]
    [InlineData("""{"claims":[{"typ":"preferred_username","val":"brdenico@contoso.com"}]}""")]
    [InlineData("""{"claims":[]}""")]
    public void A_upn_is_never_used_as_a_greeting_name(string principal)
    {
        // "Hello brdenico@contoso.com" reads as a bug. Better to greet
        // generically than to greet somebody by their email address.
        var encoded = Convert.ToBase64String(Encoding.UTF8.GetBytes(principal));

        Assert.Null(EasyAuthCustomerAccessor.ReadGivenName(encoded));
    }

    [Fact]
    public void Malformed_principal_yields_no_greeting_name()
    {
        Assert.Null(EasyAuthCustomerAccessor.ReadGivenName("not-base64!!"));
    }
}
