using BankingAgent.Api;
using BankingAgent.Application;
using BankingAgent.Infrastructure;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();
builder.Services.AddSingleton<IWorkflowService, WorkflowService>();
builder.Services.AddSingleton<IMcpClient, StubMcpClient>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapWorkflowEndpoints();

app.Run();
