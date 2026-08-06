using Azure.Core;
using Azure.Identity;
using Azure.Monitor.OpenTelemetry.AspNetCore;
using BankingAgent.Api;
using BankingAgent.Application;
using BankingAgent.Infrastructure;
using BankingAgent.Infrastructure.Persistence;
using BankingAgent.Orchestrator;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Diagnostics.HealthChecks;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Diagnostics.HealthChecks;
using Microsoft.IdentityModel.Tokens;
using Npgsql;
using OpenTelemetry.Trace;
using System.Text.Json;

const string PostgreSqlScope = "https://ossrdbms-aad.database.windows.net/.default";

var builder = WebApplication.CreateBuilder(args);
var applicationInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];
var managedIdentityClientId = builder.Configuration["AZURE_CLIENT_ID"];
var configuredServiceAuthEnabled = builder.Configuration.GetValue<bool?>("SERVICE_AUTH_ENABLED");
var serviceAuthEnabled = configuredServiceAuthEnabled ?? true;

// Some tenants forbid creating the service principal and api:// identifier URI
// that service authentication depends on, which makes the secure configuration
// impossible to provision rather than merely inconvenient. Those environments
// must opt out by name so the choice is visible in configuration and in logs,
// rather than by masquerading as Development and silently changing unrelated
// behaviour such as detailed error pages.
var allowInsecureServiceAuth = builder.Configuration.GetValue<bool?>("ALLOW_INSECURE_SERVICE_AUTH") ?? false;

if (!serviceAuthEnabled && !builder.Environment.IsDevelopment() && !allowInsecureServiceAuth)
{
    throw new InvalidOperationException(
        "SERVICE_AUTH_ENABLED=false is only permitted in Development, or in an environment that has "
        + "explicitly set ALLOW_INSECURE_SERVICE_AUTH=true to acknowledge that workflow endpoints "
        + "will accept unauthenticated callers. Deployed environments should use Entra ID service authentication.");
}

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
builder.Services.AddScoped<IWorkflowRecoveryRepository, EfWorkflowRepository>();
builder.Services.AddScoped<IWorkflowActionRepository, EfWorkflowActionRepository>();
builder.Services.AddScoped<IWorkflowEvidenceRepository, EfWorkflowEvidenceRepository>();
builder.Services.AddScoped<IWorkflowEvidenceService, WorkflowEvidenceService>();
builder.Services.AddSingleton<IDemoScenarioPolicy>(
    new DemoScenarioPolicy(builder.Configuration.GetValue<bool>("DEMO_SCENARIOS_ENABLED")));
builder.Services.Configure<FormOptions>(options =>
{
    options.MultipartBodyLengthLimit =
        (WorkflowEvidenceService.MaximumFiles * WorkflowEvidenceService.MaximumFileBytes)
        + (1024 * 1024);
});

if (serviceAuthEnabled)
{
    var tenantId = builder.Configuration["AZURE_TENANT_ID"]
        ?? throw new InvalidOperationException("AZURE_TENANT_ID is required when service authentication is enabled.");
    var orchestratorAppId = builder.Configuration["ORCHESTRATOR_APP_ID"]
        ?? throw new InvalidOperationException("ORCHESTRATOR_APP_ID is required when service authentication is enabled.");
    var v2Issuer = $"https://login.microsoftonline.com/{tenantId}/v2.0";
    var managedIdentityIssuer = $"https://sts.windows.net/{tenantId}/";
    var orchestratorAudience = $"api://{orchestratorAppId}";

    builder.Services
        .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
        .AddJwtBearer(options =>
        {
            options.Authority = v2Issuer;
            options.Audience = orchestratorAudience;
            options.MapInboundClaims = false;
            options.TokenValidationParameters = new TokenValidationParameters
            {
                ValidateIssuer = true,
                ValidIssuers = [v2Issuer, managedIdentityIssuer],
                ValidateAudience = true,
                ValidAudience = orchestratorAudience,
                ValidateLifetime = true,
                RoleClaimType = "roles",
                ClockSkew = TimeSpan.FromMinutes(1)
            };
        });
}

builder.Services.AddAuthorization(options =>
{
    options.AddPolicy("WorkflowInvoke", policy =>
    {
        if (serviceAuthEnabled)
        {
            policy.RequireRole("Workflow.Invoke");
        }
        else
        {
            policy.RequireAssertion(_ => true);
        }
    });
});

// -----------------------------------------------------------------------
// Application services
// IWorkflowService is Scoped because it depends on scoped BankingAgentDbContext
// via the repository chain.
// -----------------------------------------------------------------------
builder.Services.AddBankingAgentProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.Configure<FoundryMcpClientOptions>(options =>
{
    options.DefaultEndpoint = builder.Configuration["FOUNDRY_AGENT_ENDPOINT"];
    options.AgentName = builder.Configuration["FOUNDRY_AGENT_NAME"];
    options.Scope = builder.Configuration["FOUNDRY_SCOPE"] ?? "https://ai.azure.com/.default";
    options.ToolEndpointsJson = builder.Configuration["FOUNDRY_TOOL_ENDPOINTS"];
    options.McpToolEndpointsJson = builder.Configuration["FOUNDRY_MCP_TOOL_ENDPOINTS"];
    options.MaxAttempts = builder.Configuration.GetValue("FOUNDRY_MAX_ATTEMPTS", 3);
    options.AttemptTimeoutSeconds =
        builder.Configuration.GetValue("FOUNDRY_ATTEMPT_TIMEOUT_SECONDS", 30);
    options.BaseDelayMilliseconds =
        builder.Configuration.GetValue("FOUNDRY_RETRY_BASE_DELAY_MILLISECONDS", 250);
});
builder.Services.AddHttpClient<IMcpClient, FoundryMcpClient>();
builder.Services.AddScoped<IWorkflowService, WorkflowService>();
builder.Services.Configure<WorkflowRecoveryOptions>(options =>
{
    options.ScanIntervalSeconds =
        builder.Configuration.GetValue("WORKFLOW_RECOVERY_SCAN_INTERVAL_SECONDS", 30);
    options.StaleAfterSeconds =
        builder.Configuration.GetValue("WORKFLOW_RECOVERY_STALE_AFTER_SECONDS", 120);
    options.BatchSize =
        builder.Configuration.GetValue("WORKFLOW_RECOVERY_BATCH_SIZE", 10);
    options.MaxAttempts =
        builder.Configuration.GetValue("WORKFLOW_RECOVERY_MAX_ATTEMPTS", 5);
    options.BackoffBaseSeconds =
        builder.Configuration.GetValue("WORKFLOW_RECOVERY_BACKOFF_BASE_SECONDS", 30);
    options.BackoffMaxSeconds =
        builder.Configuration.GetValue("WORKFLOW_RECOVERY_BACKOFF_MAX_SECONDS", 900);
});
builder.Services.AddHostedService<WorkflowRecoveryWorker>();
builder.Services.AddSingleton<IWorkflowExecutionTrigger, WorkflowExecutionTrigger>();
builder.Services.AddSingleton<McpToolValidationCache>();
builder.Services.AddHealthChecks()
    .AddCheck(
        "service_auth",
        new ServiceAuthReadinessCheck(serviceAuthEnabled),
        tags: ["ready"])
    .AddCheck<PostgreSqlReadinessCheck>(
        "postgresql",
        tags: ["ready"])
    .AddCheck<FoundryConfigurationReadinessCheck>(
        "foundry_configuration",
        tags: ["ready"]);

// -----------------------------------------------------------------------
// Observability
// -----------------------------------------------------------------------
var openTelemetryBuilder = builder.Services.AddOpenTelemetry()
    .WithTracing(tracing => tracing
        .AddSource(WorkflowTelemetry.ActivitySourceName)
        .AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());
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

if (!serviceAuthEnabled)
{
    var reason = allowInsecureServiceAuth && !app.Environment.IsDevelopment()
        ? "ALLOW_INSECURE_SERVICE_AUTH=true was set for a deployed environment"
        : "running in Development";

    app.Logger.LogWarning(
        "************************************************************************************************************************");
    app.Logger.LogWarning(
        "INSECURE CONFIGURATION: SERVICE_AUTH_ENABLED=false ({Reason}). Workflow endpoints accept unauthenticated callers.",
        reason);
    app.Logger.LogWarning(
        "Anyone who can reach this ingress can start and approve workflows. Do not use for real or regulated data.");
    app.Logger.LogWarning(
        "************************************************************************************************************************");
}

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseBankingAgentProblemDetails();
if (serviceAuthEnabled)
{
    app.UseAuthentication();
}
app.UseAuthorization();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapHealthChecks("/health/live", new HealthCheckOptions
{
    Predicate = _ => false
});
app.MapHealthChecks("/health/ready", new HealthCheckOptions
{
    Predicate = registration => registration.Tags.Contains("ready"),
    ResponseWriter = WriteReadyHealthResponse
});
app.MapWorkflowEndpoints();

app.Run();

static Task WriteReadyHealthResponse(HttpContext context, HealthReport report)
{
    context.Response.ContentType = "application/json";
    return context.Response.WriteAsync(JsonSerializer.Serialize(new
    {
        status = report.Status.ToString(),
        checks = report.Entries.ToDictionary(
            entry => entry.Key,
            entry => new
            {
                status = entry.Value.Status.ToString(),
                description = entry.Value.Description,
            })
    }));
}
