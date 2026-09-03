using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using BankingAgent.WebUi;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.Identity.Web;
using OpenTelemetry.Trace;

var builder = WebApplication.CreateBuilder(args);
var orchestratorApiBaseUrl = builder.Configuration["ORCHESTRATOR_API_BASE_URL"] ?? "http://localhost:5000";
var applicationInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
var dataProtectionKeysPath = builder.Configuration["DATA_PROTECTION_KEYS_PATH"]
    ?? Path.Combine(Path.GetTempPath(), "banking-agent-data-protection");

if (!Uri.TryCreate(orchestratorApiBaseUrl, UriKind.Absolute, out var orchestratorApiBaseUri))
{
    throw new InvalidOperationException("ORCHESTRATOR_API_BASE_URL must be an absolute URL.");
}

// Read early because it decides which identity source the rest of the
// container is built around.
var userDelegationEnabled = builder.Configuration.GetValue<bool?>("USER_DELEGATION_ENABLED") ?? false;

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();

if (!userDelegationEnabled)
{
    builder.Services.AddSingleton<ISignedInCustomerAccessor, EasyAuthCustomerAccessor>();
}
builder.Services.AddHttpClient("orchestrator-health", client =>
{
    client.BaseAddress = orchestratorApiBaseUri;
});
builder.Services.AddHealthChecks()
    .AddCheck<OrchestratorReadinessCheck>(
        "orchestrator",
        tags: ["ready"]);
builder.Services.AddTransient<CorrelationIdHandler>();
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders =
        ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;
    options.ForwardLimit = 1;
    options.KnownIPNetworks.Clear();
    options.KnownProxies.Clear();
});
builder.Services.AddAntiforgery(options =>
{
    options.Cookie.Name = builder.Environment.IsDevelopment()
        ? "BankingAgent.Antiforgery.v2"
        : "__Host-BankingAgent.Antiforgery.v2";
    options.Cookie.HttpOnly = true;
    options.Cookie.Path = "/";
    options.Cookie.SameSite = SameSiteMode.Strict;
    options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
        ? CookieSecurePolicy.SameAsRequest
        : CookieSecurePolicy.Always;
});

var orchestratorTokenScope = builder.Configuration["ORCHESTRATOR_TOKEN_SCOPE"];
var azureClientId = builder.Configuration["AZURE_CLIENT_ID"];

// Call the orchestrator as the signed-in customer rather than as the
// application. Off by default, so every existing deployment keeps the behaviour
// it has: Container Apps built-in authentication in front, and the customer
// identifier passed onward as a value the orchestrator trusts.
if (userDelegationEnabled)
{
    var apiScope = builder.Configuration["ORCHESTRATOR_API_SCOPE"];
    var clientId = builder.Configuration["WEBUI_AUTH_CLIENT_ID"];
    var clientSecret = builder.Configuration["WEBUI_AUTH_CLIENT_SECRET"];
    var tenantId = builder.Configuration["WEBUI_AUTH_TENANT_ID"];

    ArgumentException.ThrowIfNullOrWhiteSpace(apiScope, "ORCHESTRATOR_API_SCOPE");
    ArgumentException.ThrowIfNullOrWhiteSpace(clientId, "WEBUI_AUTH_CLIENT_ID");
    ArgumentException.ThrowIfNullOrWhiteSpace(clientSecret, "WEBUI_AUTH_CLIENT_SECRET");
    ArgumentException.ThrowIfNullOrWhiteSpace(tenantId, "WEBUI_AUTH_TENANT_ID");

    builder.Services
        .AddAuthentication(OpenIdConnectDefaults.AuthenticationScheme)
        .AddMicrosoftIdentityWebApp(options =>
        {
            options.Instance = "https://login.microsoftonline.com/";
            options.TenantId = tenantId;
            options.ClientId = clientId;
            options.ClientSecret = clientSecret;
            options.CallbackPath = "/signin-oidc";
            options.SignedOutCallbackPath = "/signout-oidc";

            // The guard on the orchestrator reads the short "oid" claim. Claim
            // mapping would rewrite it to a schema URI and the guard would
            // reject every customer.
            options.MapInboundClaims = false;
        })
        // Requests the orchestrator scope during sign-in, so the authorization
        // code redeems into a refresh token that covers it and the token cache
        // can serve orchestrator tokens without another interactive prompt.
        .EnableTokenAcquisitionToCallDownstreamApi([apiScope])
        // Per-process and lost on restart, which costs a re-sign-in and nothing
        // else. A distributed cache would need a backing store, and every store
        // available here is either a key at rest or blocked by the same policy
        // that ruled out the Easy Auth token store.
        .AddInMemoryTokenCaches();

    builder.Services.AddAuthorization();

    // Sign-in is required to use the application at all, but not to answer the
    // platform's probes; those are mapped outside the Razor Pages pipeline and
    // so are unaffected by this convention.
    builder.Services.Configure<RazorPagesOptions>(options =>
    {
        options.Conventions.AuthorizeFolder("/");
        // Reachable while unauthenticated by definition, and requiring sign-in
        // to view an error page turns any failure into a redirect loop.
        options.Conventions.AllowAnonymousToPage("/Error");
    });

    builder.Services.AddSingleton<ISignedInCustomerAccessor, ClaimsPrincipalCustomerAccessor>();
    builder.Services.AddTransient(sp => new DelegatedUserTokenHandler(
        sp.GetRequiredService<IHttpContextAccessor>(),
        apiScope));

    builder.Services.AddHttpClient("orchestrator", client =>
    {
        client.BaseAddress = orchestratorApiBaseUri;
    })
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<DelegatedUserTokenHandler>();
}
else if (!string.IsNullOrWhiteSpace(orchestratorTokenScope))
{
    if (!builder.Environment.IsDevelopment())
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(azureClientId, "AZURE_CLIENT_ID");
    }

    TokenCredential credential = builder.Environment.IsDevelopment()
        ? new DefaultAzureCredential()
        : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(azureClientId!));

    builder.Services.AddSingleton(credential);
    builder.Services.AddTransient(sp =>
        new OrchestratorTokenHandler(sp.GetRequiredService<TokenCredential>(), orchestratorTokenScope));
    builder.Services.AddHttpClient("orchestrator", client =>
    {
        client.BaseAddress = orchestratorApiBaseUri;
    })
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<OrchestratorTokenHandler>();
}
else
{
    builder.Services.AddHttpClient("orchestrator", client =>
    {
        client.BaseAddress = orchestratorApiBaseUri;
    }).AddHttpMessageHandler<CorrelationIdHandler>();
}

var openTelemetryBuilder = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());
if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
{
    openTelemetryBuilder.UseAzureMonitor();
}

Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("BankingAgent.WebUi")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

var app = builder.Build();

app.UseForwardedHeaders();
app.UseMiddleware<WebUiCorrelationMiddleware>();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.MapRazorPages();

if (userDelegationEnabled)
{
    // Both schemes, deliberately. Clearing only the local cookie would leave the
    // Entra session intact, so the next sign-in would complete silently and the
    // user would appear unable to sign out.
    app.MapGet("/signout", () => Results.SignOut(
        new AuthenticationProperties { RedirectUri = "/" },
        [CookieAuthenticationDefaults.AuthenticationScheme, OpenIdConnectDefaults.AuthenticationScheme]));
}

app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready")
});

app.Run();

// Expose Program for WebApplicationFactory<Program> in integration tests.
public partial class Program { }
