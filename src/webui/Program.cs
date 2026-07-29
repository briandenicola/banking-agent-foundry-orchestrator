var builder = WebApplication.CreateBuilder(args);
var orchestratorApiBaseUrl = builder.Configuration["ORCHESTRATOR_API_BASE_URL"] ?? "http://localhost:5000";

if (!Uri.TryCreate(orchestratorApiBaseUrl, UriKind.Absolute, out var orchestratorApiBaseUri))
{
    throw new InvalidOperationException("ORCHESTRATOR_API_BASE_URL must be an absolute URL.");
}

builder.Services.AddRazorPages();
builder.Services.AddHttpClient("orchestrator", client =>
{
    client.BaseAddress = orchestratorApiBaseUri;
});

var app = builder.Build();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
}

app.UseStaticFiles();
app.UseRouting();
app.UseAuthorization();
app.MapRazorPages();

app.Run();
