using System.Text;
using System.Text.Json;

namespace BankingAgent.WebUi;

/// <summary>
/// The person using the Web UI, as established by Container Apps built-in
/// authentication ("Easy Auth").
/// </summary>
public sealed record SignedInCustomer(string Id, string? DisplayName)
{
    /// <summary>Nobody is signed in, or the deployment has no authentication.</summary>
    public static readonly SignedInCustomer Anonymous = new(string.Empty, null);

    public bool IsAuthenticated => Id.Length > 0;
}

public interface ISignedInCustomerAccessor
{
    SignedInCustomer Current { get; }
}

/// <summary>
/// Reads the signed-in user from the headers Easy Auth injects.
///
/// Why this is gated on configuration
/// ----------------------------------
/// These headers are only trustworthy when the platform is terminating
/// authentication in front of the container, because it is the platform that
/// strips any client-supplied copy and replaces it with the verified one. With
/// authentication switched off the container is reachable directly and the
/// headers become caller-controlled, so honouring them would let anyone assert
/// any identity and read another customer's remembered profile. That is a worse
/// outcome than having no personalisation, so the accessor reports nobody
/// rather than trusting an unverified header.
/// </summary>
public sealed class EasyAuthCustomerAccessor : ISignedInCustomerAccessor
{
    // Set by the platform on every authenticated request.
    private const string PrincipalIdHeader = "X-MS-CLIENT-PRINCIPAL-ID";
    private const string PrincipalNameHeader = "X-MS-CLIENT-PRINCIPAL-NAME";

    // Base64 JSON of the full principal, used only as a fallback when the
    // convenience headers are absent.
    private const string PrincipalHeader = "X-MS-CLIENT-PRINCIPAL";

    private readonly IHttpContextAccessor _httpContextAccessor;
    private readonly bool _authenticationEnabled;

    public EasyAuthCustomerAccessor(IHttpContextAccessor httpContextAccessor, IConfiguration configuration)
    {
        _httpContextAccessor = httpContextAccessor;
        _authenticationEnabled = configuration.GetValue("WEBUI_AUTH_ENABLED", false);
    }

    public SignedInCustomer Current
    {
        get
        {
            if (!_authenticationEnabled)
            {
                return SignedInCustomer.Anonymous;
            }

            var headers = _httpContextAccessor.HttpContext?.Request.Headers;
            if (headers is null)
            {
                return SignedInCustomer.Anonymous;
            }

            var id = headers[PrincipalIdHeader].ToString();
            var name = headers[PrincipalNameHeader].ToString();

            if (string.IsNullOrWhiteSpace(id))
            {
                (id, name) = ReadEncodedPrincipal(headers[PrincipalHeader].ToString());
            }

            return string.IsNullOrWhiteSpace(id)
                ? SignedInCustomer.Anonymous
                : new SignedInCustomer(id.Trim(), string.IsNullOrWhiteSpace(name) ? null : name.Trim());
        }
    }

    /// <summary>
    /// Pulls the object identifier out of the base64 principal blob. Malformed
    /// input yields no identity: a request that cannot be attributed with
    /// confidence is treated as anonymous rather than guessed at.
    /// </summary>
    internal static (string Id, string? Name) ReadEncodedPrincipal(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return (string.Empty, null);
        }

        JsonElement root;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException)
        {
            return (string.Empty, null);
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("claims", out var claims) ||
            claims.ValueKind != JsonValueKind.Array)
        {
            return (string.Empty, null);
        }

        string id = string.Empty;
        string? name = null;
        foreach (var claim in claims.EnumerateArray())
        {
            if (claim.ValueKind != JsonValueKind.Object ||
                !claim.TryGetProperty("typ", out var type) ||
                !claim.TryGetProperty("val", out var value) ||
                type.ValueKind != JsonValueKind.String ||
                value.ValueKind != JsonValueKind.String)
            {
                continue;
            }

            switch (type.GetString())
            {
                case "http://schemas.microsoft.com/identity/claims/objectidentifier":
                case "oid":
                    id = value.GetString() ?? string.Empty;
                    break;
                case "preferred_username":
                case "name":
                    name ??= value.GetString();
                    break;
            }
        }

        return (id, name);
    }
}
