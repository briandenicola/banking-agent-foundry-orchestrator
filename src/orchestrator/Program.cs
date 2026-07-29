var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/health", () => Results.Ok(new { status = "ok" }));
app.MapPost("/workflow", async (WorkflowRequest request) =>
{
    var traceId = Guid.NewGuid().ToString("N");
    return Results.Ok(new WorkflowResponse(
        traceId,
        "received",
        "Workflow accepted. Agent execution will be routed through the Python services."));
});

app.Run();

public record WorkflowRequest(string UserMessage);
public record WorkflowResponse(string TraceId, string Status, string Message);
