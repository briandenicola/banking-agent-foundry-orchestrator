using Azure.Core;
using Azure.Identity;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;

var builder = WebApplication.CreateBuilder(args);
var orchestratorApiBaseUrl = builder.Configuration["ORCHESTRATOR_API_BASE_URL"] ?? "http://localhost:5000";
var dataProtectionKeysPath = builder.Configuration["DATA_PROTECTION_KEYS_PATH"]
    ?? Path.Combine(Path.GetTempPath(), "banking-agent-data-protection");

if (!Uri.TryCreate(orchestratorApiBaseUrl, UriKind.Absolute, out var orchestratorApiBaseUri))
{
    throw new InvalidOperationException("ORCHESTRATOR_API_BASE_URL must be an absolute URL.");
}

builder.Services.AddRazorPages();
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

if (!builder.Environment.IsDevelopment())
{
    ArgumentException.ThrowIfNullOrWhiteSpace(azureClientId, "AZURE_CLIENT_ID");
    ArgumentException.ThrowIfNullOrWhiteSpace(orchestratorTokenScope, "ORCHESTRATOR_TOKEN_SCOPE");
}

TokenCredential credential = builder.Environment.IsDevelopment()
    ? new DefaultAzureCredential()
    : new ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(azureClientId!));

builder.Services.AddSingleton(credential);

if (!string.IsNullOrEmpty(orchestratorTokenScope))
{
    builder.Services.AddTransient(sp =>
        new OrchestratorTokenHandler(sp.GetRequiredService<TokenCredential>(), orchestratorTokenScope));
    builder.Services.AddHttpClient("orchestrator", client =>
    {
        client.BaseAddress = orchestratorApiBaseUri;
    }).AddHttpMessageHandler<OrchestratorTokenHandler>();
}
else
{
    builder.Services.AddHttpClient("orchestrator", client =>
    {
        client.BaseAddress = orchestratorApiBaseUri;
    });
}

Directory.CreateDirectory(dataProtectionKeysPath);
builder.Services
    .AddDataProtection()
    .SetApplicationName("BankingAgent.WebUi")
    .PersistKeysToFileSystem(new DirectoryInfo(dataProtectionKeysPath));

var app = builder.Build();

app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
