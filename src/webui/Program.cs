using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using BankingAgent.WebUi;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.Identity.Client;
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

builder.Services.AddRazorPages();
builder.Services.AddHttpContextAccessor();
builder.Services.AddSingleton<ISignedInCustomerAccessor, EasyAuthCustomerAccessor>();
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

// On-behalf-of: call the orchestrator as the signed-in customer rather than as
// the application. Off by default, so every existing deployment keeps the
// behaviour it has.
var oboEnabled = builder.Configuration.GetValue<bool?>("OBO_ENABLED") ?? false;

if (oboEnabled)
{
    var oboScope = builder.Configuration["ORCHESTRATOR_OBO_SCOPE"];
    var oboClientId = builder.Configuration["WEBUI_AUTH_CLIENT_ID"];
    var oboClientSecret = builder.Configuration["WEBUI_AUTH_CLIENT_SECRET"];
    var oboTenantId = builder.Configuration["WEBUI_AUTH_TENANT_ID"];

    ArgumentException.ThrowIfNullOrWhiteSpace(oboScope, "ORCHESTRATOR_OBO_SCOPE");
    ArgumentException.ThrowIfNullOrWhiteSpace(oboClientId, "WEBUI_AUTH_CLIENT_ID");
    ArgumentException.ThrowIfNullOrWhiteSpace(oboClientSecret, "WEBUI_AUTH_CLIENT_SECRET");
    ArgumentException.ThrowIfNullOrWhiteSpace(oboTenantId, "WEBUI_AUTH_TENANT_ID");

    // Registered as a singleton for its token cache: the exchange is a network
    // round trip to Entra, and a per-request client would repeat it on every
    // page load and invite throttling.
    builder.Services.AddSingleton(_ => ConfidentialClientApplicationBuilder
        .Create(oboClientId)
        .WithClientSecret(oboClientSecret)
        .WithAuthority($"https://login.microsoftonline.com/{oboTenantId}")
        .Build());

    builder.Services.AddTransient(sp => new OnBehalfOfTokenHandler(
        sp.GetRequiredService<IConfidentialClientApplication>(),
        sp.GetRequiredService<IHttpContextAccessor>(),
        oboScope,
        sp.GetRequiredService<ILogger<OnBehalfOfTokenHandler>>()));

    builder.Services.AddHttpClient("orchestrator", client =>
    {
        client.BaseAddress = orchestratorApiBaseUri;
    })
        .AddHttpMessageHandler<CorrelationIdHandler>()
        .AddHttpMessageHandler<OnBehalfOfTokenHandler>();
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
app.UseAuthorization();
app.MapRazorPages();
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
