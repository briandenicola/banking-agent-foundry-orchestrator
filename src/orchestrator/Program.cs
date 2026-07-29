using Azure.Monitor.OpenTelemetry.AspNetCore;
using BankingAgent.Api;
using BankingAgent.Application;
using BankingAgent.Infrastructure;
using BankingAgent.Orchestrator;

var builder = WebApplication.CreateBuilder(args);
var applicationInsightsConnectionString = builder.Configuration["APPLICATIONINSIGHTS_CONNECTION_STRING"];

builder.Services.AddProblemDetails();
builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddHttpClient<IMcpClient, FoundryMcpClient>();
builder.Services.AddSingleton<IWorkflowService, WorkflowService>();

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

var app = builder.Build();

app.UseMiddleware<CorrelationIdMiddleware>();
app.UseExceptionHandler();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWorkflowEndpoints();

app.Run();
