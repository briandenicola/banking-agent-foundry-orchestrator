using System.Security.Claims;
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

    /// <summary>
    /// First name, for greeting the person by name. Null when the token
    /// carried nothing suitable, in which case callers should greet generically
    /// rather than fall back to the UPN: "Hello brdenico@contoso.com" reads as
    /// a bug.
    /// </summary>
    public string? GivenName { get; init; }

    public bool IsAuthenticated => Id.Length > 0;
}

public interface ISignedInCustomerAccessor
{
    SignedInCustomer Current { get; }
}

/// <summary>
/// Reads the signed-in user from the authenticated principal, for deployments
/// where the Web UI runs its own OpenID Connect sign-in rather than sitting
/// behind Container Apps built-in authentication.
///
/// Nothing here is gated on a configuration flag, unlike
/// <see cref="EasyAuthCustomerAccessor"/>. The difference is where the identity
/// comes from: a header can be forged by any caller that reaches the container,
/// so honouring it is only safe while the platform is verifying and replacing
/// it, whereas this principal was built by the authentication handler in this
/// process from a token it validated itself. There is no configuration under
/// which it is untrustworthy.
/// </summary>
public sealed class ClaimsPrincipalCustomerAccessor(IHttpContextAccessor httpContextAccessor)
    : ISignedInCustomerAccessor
{
    public SignedInCustomer Current
    {
        get
        {
            var principal = httpContextAccessor.HttpContext?.User;

            if (principal?.Identity?.IsAuthenticated != true)
            {
                return SignedInCustomer.Anonymous;
            }

            var id = Read(principal, "oid")
                ?? Read(principal, "http://schemas.microsoft.com/identity/claims/objectidentifier");

            if (string.IsNullOrWhiteSpace(id))
            {
                return SignedInCustomer.Anonymous;
            }

            var name = Read(principal, "preferred_username") ?? Read(principal, "name");

            return new SignedInCustomer(id.Trim(), string.IsNullOrWhiteSpace(name) ? null : name.Trim())
            {
                GivenName = ReadGivenName(principal),
            };
        }
    }

    /// <summary>
    /// A first name to greet the user by, or null when the token offers none.
    /// Never falls back to the UPN, which is an address rather than a name.
    /// </summary>
    private static string? ReadGivenName(ClaimsPrincipal principal)
    {
        var given = Read(principal, "given_name") ?? Read(principal, ClaimTypes.GivenName);
        if (!string.IsNullOrWhiteSpace(given))
        {
            return given.Trim();
        }

        var displayName = Read(principal, "name");
        if (string.IsNullOrWhiteSpace(displayName) || displayName.Contains('@'))
        {
            return null;
        }

        var first = displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0].Trim();
        return first.Length == 0 ? null : first;
    }

    private static string? Read(ClaimsPrincipal principal, string type) =>
        principal.FindFirst(type)?.Value;
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
            var encoded = headers[PrincipalHeader].ToString();

            if (string.IsNullOrWhiteSpace(id))
            {
                (id, name) = ReadEncodedPrincipal(encoded);
            }

            // Always read the blob for the given name. The convenience header
            // carries the UPN, which is not a name to greet somebody by.
            return string.IsNullOrWhiteSpace(id)
                ? SignedInCustomer.Anonymous
                : new SignedInCustomer(id.Trim(), string.IsNullOrWhiteSpace(name) ? null : name.Trim())
                {
                    GivenName = ReadGivenName(encoded),
                };
        }
    }

    /// <summary>
    /// Pulls the object identifier out of the base64 principal blob. Malformed
    /// input yields no identity: a request that cannot be attributed with
    /// confidence is treated as anonymous rather than guessed at.
    /// </summary>
    internal static (string Id, string? Name) ReadEncodedPrincipal(string? encoded)
    {
        if (ParseClaims(encoded) is not { } claims)
        {
            return (string.Empty, null);
        }

        string id = string.Empty;
        string? name = null;
        foreach (var (type, value) in claims)
        {
            switch (type)
            {
                case "http://schemas.microsoft.com/identity/claims/objectidentifier":
                case "oid":
                    id = value;
                    break;
                case "preferred_username":
                case "name":
                    name ??= value;
                    break;
            }
        }

        return (id, name);
    }

    /// <summary>
    /// A first name to greet the user by, or null when the token offers none.
    /// Prefers the <c>given_name</c> claim and falls back to the first word of
    /// the display name. Never falls back to the UPN, which is an address
    /// rather than a name.
    /// </summary>
    internal static string? ReadGivenName(string? encoded)
    {
        if (ParseClaims(encoded) is not { } claims)
        {
            return null;
        }

        string? displayName = null;
        foreach (var (type, value) in claims)
        {
            switch (type)
            {
                case "http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname":
                case "given_name":
                    return Clean(value);
                case "name":
                    displayName ??= value;
                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(displayName) || displayName.Contains('@'))
        {
            return null;
        }

        return Clean(displayName.Split(' ', StringSplitOptions.RemoveEmptyEntries)[0]);

        static string? Clean(string candidate)
        {
            var trimmed = candidate.Trim();
            return trimmed.Length == 0 ? null : trimmed;
        }
    }

    /// <summary>
    /// Decodes the principal blob into its claim pairs. Malformed input yields
    /// nothing: a request that cannot be attributed with confidence is treated
    /// as anonymous rather than guessed at.
    /// </summary>
    private static List<(string Type, string Value)>? ParseClaims(string? encoded)
    {
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return null;
        }

        JsonElement root;
        try
        {
            var json = Encoding.UTF8.GetString(Convert.FromBase64String(encoded));
            root = JsonDocument.Parse(json).RootElement;
        }
        catch (Exception exception) when (exception is FormatException or JsonException or DecoderFallbackException)
        {
            return null;
        }

        if (root.ValueKind != JsonValueKind.Object ||
            !root.TryGetProperty("claims", out var claims) ||
            claims.ValueKind != JsonValueKind.Array)
        {
            return null;
        }

        var parsed = new List<(string Type, string Value)>();
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

            if (type.GetString() is { } typeText && value.GetString() is { } valueText)
            {
                parsed.Add((typeText, valueText));
            }
        }

        return parsed;
    }
}
