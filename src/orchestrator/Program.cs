using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using BankingAgent.Api;
using BankingAgent.Application;
using BankingAgent.Infrastructure;
using BankingAgent.Infrastructure.Persistence;
using BankingAgent.Orchestrator;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.EntityFrameworkCore;
using Microsoft.IdentityModel.Tokens;
using Npgsql;

const string PostgreSqlScope = "https://ossrdbms-aad.database.windows.net/.default";

var builder = WebApplication.CreateBuilder(args);
var applicationInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];
var tenantId = builder.Configuration["AZURE_TENANT_ID"]
    ?? throw new InvalidOperationException("AZURE_TENANT_ID is required.");
var orchestratorAppId = builder.Configuration["ORCHESTRATOR_APP_ID"]
    ?? throw new InvalidOperationException("ORCHESTRATOR_APP_ID is required.");

// -----------------------------------------------------------------------
// PostgreSQL / EF Core
// -----------------------------------------------------------------------
// SslMode: Require in Azure (AZURE_CLIENT_ID set), Prefer locally so
// developers without a TLS-terminated postgres don't need extra setup.
var postgresqlHost = builder.Configuration["POSTGRESQL_HOST"] ?? "localhost";
var postgresqlDatabase = builder.Configuration["POSTGRESQL_DATABASE"] ?? "banking_agent";
var postgresqlUser = builder.Configuration["POSTGRESQL_USER"] ?? "postgres";
var useManagedIdentity = !string.IsNullOrWhiteSpace(managedIdentityClientId);

var connectionString = new NpgsqlConnectionStringBuilder
{
    Host = postgresqlHost,
    Database = postgresqlDatabase,
    Username = postgresqlUser,
    SslMode = useManagedIdentity ? SslMode.Require : SslMode.Prefer
}.ConnectionString;

var dataSourceBuilder = new NpgsqlDataSourceBuilder(connectionString);

if (useManagedIdentity)
{
    var credential = new ManagedIdentityCredential(
        ManagedIdentityId.FromUserAssignedClientId(managedIdentityClientId!));

    dataSourceBuilder.UsePeriodicPasswordProvider(
        async (_, ct) =>
        {
            var token = await credential.GetTokenAsync(
                new TokenRequestContext([PostgreSqlScope]), ct);
            return token.Token;
        },
        successRefreshInterval: TimeSpan.FromMinutes(4),
        failureRefreshInterval: TimeSpan.FromSeconds(30));
}

// Register as singleton so the connection pool and token-refresh timer
// survive across scoped DbContext instances. Migrations must be run via
// the database-migrator Container Apps Job — never on startup here.
var npgsqlDataSource = dataSourceBuilder.Build();
builder.Services.AddSingleton(_ => npgsqlDataSource);

builder.Services.AddDbContext<BankingAgentDbContext>((sp, options) =>
    options.UseNpgsql(sp.GetRequiredService<NpgsqlDataSource>()));

builder.Services.AddScoped<IWorkflowRepository, EfWorkflowRepository>();
builder.Services.AddScoped<IWorkflowActionRepository, EfWorkflowActionRepository>();

builder.Services
    .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
    .AddJwtBearer(options =>
    {
        options.Authority = $"https://login.microsoftonline.com/{tenantId}/v2.0";
        options.Audience = $"api://{orchestratorAppId}";
        options.TokenValidationParameters = new TokenValidationParameters
        {
            ValidateIssuer = true,
            ValidIssuer = $"https://login.microsoftonline.com/{tenantId}/v2.0",
            ValidateAudience = true,
            ValidAudience = $"api://{orchestratorAppId}",
            ValidateLifetime = true,
            ClockSkew = TimeSpan.FromMinutes(1)
        };
    });
builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WorkflowInvoke", policy =>
        policy.RequireRole("Workflow.Invoke"));
});

// -----------------------------------------------------------------------
// Application services
// IWorkflowService is Scoped because it depends on scoped BankingAgentDbContext
// via the repository chain.
// -----------------------------------------------------------------------
builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient<IMcpClient, FoundryMcpClient>();
builder.Services.AddScoped<IWorkflowService, WorkflowService>();

// -----------------------------------------------------------------------
// Observability
// -----------------------------------------------------------------------
var openTelemetryBuilder = builder.Services.AddOpenTelemetry();
if (!string.IsNullOrWhiteSpace(applicationInsightsConnectionString))
{
    openTelemetryBuilder.UseAzureMonitor();
}

builder.Logging.ClearProviders();
builder.Logging.AddSimpleConsole(options =>
{
    options.SingleLine = true;
    options.TimestampFormat = "HH:mm:ss ";
});

// -----------------------------------------------------------------------
// Middleware and endpoints
// -----------------------------------------------------------------------
var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();
app.UseAuthentication();
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWorkflowEndpoints();

app.Run();
