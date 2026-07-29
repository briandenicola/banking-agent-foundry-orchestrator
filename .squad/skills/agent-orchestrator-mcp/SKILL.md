# Skill: Agent Orchestrator with MCP Tool Integration

**Category:** Architecture  
**Difficulty:** High  
**Tags:** agent-framework, mcp, layered-architecture, c-sharp  

## Problem

How do you structure a C# orchestrator agent that:
- Invokes remote reasoning agents (e.g., LangGraph on Microsoft Foundry) as MCP tools
- Maintains correlation IDs and audit trails through the entire workflow
- Enforces approval gates before sensitive actions
- Follows a strict layered architecture (Domain → Application → Infrastructure → API)
- Never uses API keys for service-to-service auth (Entra ID only)

## Solution Pattern

### 1. Core Abstractions (Domain Layer)

Define domain models and contracts that are framework-agnostic:

```csharp
// Domain models
public record WorkflowRequest(string RequestId, string UserMessage);
public record WorkflowState(
    string TraceId,
    WorkflowStatus Status,
    List<AuditEvent> Events,
    List<ApprovalGate> Approvals);

public enum WorkflowStatus { Received, Processing, ApprovalRequired, Executed, Failed }

public record AuditEvent(
    DateTime Timestamp,
    string Action,
    string Actor,
    object Result,
    string? Error = null);

public record ApprovalGate(
    string Id,
    string Description,
    bool IsApproved,
    string? ApprovedBy = null,
    DateTime? ApprovedAt = null);

// Contract for MCP integration
public interface IMcpClient
{
    Task<ToolDiscoveryResponse> DiscoverToolsAsync(CancellationToken ct);
    Task<object> InvokeTool(string toolName, Dictionary<string, object> parameters, CancellationToken ct);
}
```

**Why:** Keeping domain models framework-agnostic enables easier testing, swapping implementations, and avoiding tight coupling to MCP library versions.

### 2. Application Layer (Orchestration)

Implement the workflow state machine and coordination logic:

```csharp
public class WorkflowOrchestrationService
{
    private readonly IMcpClient _mcpClient;
    private readonly ILogger<WorkflowOrchestrationService> _logger;

    public async Task<WorkflowState> SubmitWorkflow(WorkflowRequest request)
    {
        var traceId = Guid.NewGuid().ToString("N");
        var state = new WorkflowState(
            traceId,
            WorkflowStatus.Received,
            new() { new AuditEvent(DateTime.UtcNow, "workflow_received", "user", request) },
            new());

        using var activity = new Activity("workflow_orchestration").Start();
        activity.SetTag("trace_id", traceId);

        try
        {
            _logger.LogInformation("Workflow {TraceId} received: {Message}", traceId, request.UserMessage);

            // Simulate agent planning (Phase 1: hardcoded; Phase 2: use Agent Framework)
            var tools = await _mcpClient.DiscoverToolsAsync();
            var result = await _mcpClient.InvokeTool("analyze_request", 
                new() { { "message", request.UserMessage } });

            state = state with 
            { 
                Status = WorkflowStatus.Processing,
                Events = state.Events.Concat(new[] {
                    new AuditEvent(DateTime.UtcNow, "mcp_tool_invoked", "system", result)
                }).ToList()
            };

            // Check if approval is needed (banking rule example)
            if (NeedsBankingApproval(result))
            {
                state = state with
                {
                    Status = WorkflowStatus.ApprovalRequired,
                    Approvals = new() { new ApprovalGate("sensitive_action", "Banking action requires approval") }
                };
            }

            return state;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Workflow {TraceId} failed", traceId);
            state = state with
            {
                Status = WorkflowStatus.Failed,
                Events = state.Events.Concat(new[] {
                    new AuditEvent(DateTime.UtcNow, "workflow_failed", "system", null, ex.Message)
                }).ToList()
            };
            throw;
        }
    }

    private bool NeedsBankingApproval(object result) => /* implement rule */;
}
```

**Why:** Separates orchestration logic from HTTP handling and MCP mechanics. Easier to test state transitions.

### 3. Infrastructure Layer (MCP Integration)

Implement the MCP client with Foundry-specific details:

```csharp
public class FoundryMcpClient : IMcpClient
{
    private readonly HttpClient _httpClient;
    private readonly ILogger<FoundryMcpClient> _logger;
    private static readonly Dictionary<string, MCPToolDefinition> _toolRegistry = new()
    {
        {
            "analyze_request",
            new(
                Name: "analyze_request",
                Description: "Analyze banking request for intent and risk",
                Parameters: new() { { "message", "string" } },
                FoundryAgentUrl: "https://foundry-agents-dev.azurecontainerapps.io/agents/analyzer")
        }
    };

    public async Task<ToolDiscoveryResponse> DiscoverToolsAsync(CancellationToken ct)
    {
        // Phase 1: Return hardcoded registry
        // Phase 2: Call Foundry API for dynamic discovery
        return new(_toolRegistry.Keys.ToList(), _toolRegistry);
    }

    public async Task<object> InvokeTool(string toolName, Dictionary<string, object> parameters, CancellationToken ct)
    {
        if (!_toolRegistry.TryGetValue(toolName, out var tool))
            throw new InvalidOperationException($"Tool {toolName} not found");

        using var activity = new Activity("mcp_tool_invocation").Start();
        activity.SetTag("tool_name", toolName);

        try
        {
            var request = new HttpRequestMessage(HttpMethod.Post, tool.FoundryAgentUrl)
            {
                Content = JsonContent.Create(parameters)
            };
            // Add Entra ID token via DefaultAzureCredential
            var token = await new DefaultAzureCredential().GetTokenAsync(
                new("https://management.azure.com/.default"), ct);
            request.Headers.Authorization = new("Bearer", token.Token);

            var response = await _httpClient.SendAsync(request, ct);
            response.EnsureSuccessStatusCode();

            var result = await response.Content.ReadFromJsonAsync<object>();
            _logger.LogInformation("Tool {ToolName} invoked successfully", toolName);
            return result;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Tool {ToolName} invocation failed", toolName);
            throw;
        }
    }
}

public record MCPToolDefinition(string Name, string Description, Dictionary<string, string> Parameters, string FoundryAgentUrl);
```

**Why:** Isolates Foundry-specific code. Enables swapping with mock/stub clients for testing. All auth goes through Azure Identity libraries (no secrets).

### 4. API Layer (Controllers)

Keep endpoints thin:

```csharp
[ApiController]
[Route("api/v1/workflow")]
public class WorkflowController(WorkflowOrchestrationService orchestrationService)
{
    [HttpPost]
    public async Task<WorkflowResponse> SubmitWorkflow([FromBody] WorkflowRequest request)
    {
        var state = await orchestrationService.SubmitWorkflow(request);
        return new WorkflowResponse(
            state.TraceId,
            state.Status.ToString(),
            $"Workflow {state.Status}",
            state.Approvals.Any() && !state.Approvals[0].IsApproved,
            state.Events);
    }

    [HttpGet("{traceId}")]
    public async Task<WorkflowResponse> GetStatus(string traceId)
    {
        // Fetch from state store (HorizonDB Phase 2)
        // Return current state and audit trail
        throw new NotImplementedException();
    }

    [HttpPost("{traceId}/approve")]
    public async Task<WorkflowResponse> ApproveAction(string traceId, [FromBody] ApprovalRequest approval)
    {
        // Persist approval, resume workflow
        throw new NotImplementedException();
    }
}

public record WorkflowResponse(
    string TraceId,
    string Status,
    string Message,
    bool ApprovalRequired,
    List<AuditEvent> Events);
```

**Why:** Controllers delegate to application services. No business logic in handlers. All errors handled at middleware level with ProblemDetails.

### 5. Dependency Injection (Program.cs)

Wire everything together:

```csharp
var builder = WebApplication.CreateBuilder(args);

// Infrastructure
builder.Services.AddSingleton<IMcpClient, FoundryMcpClient>();
builder.Services.AddHttpClient<FoundryMcpClient>();
builder.Services.AddAzureClients(cfg => cfg.AddDefaultCredential());  // Managed Identity

// Application
builder.Services.AddScoped<WorkflowOrchestrationService>();

// Observability
builder.Services.AddOpenTelemetry()
    .WithTracing(cfg => cfg.AddAspNetCoreInstrumentation()
        .AddHttpClientInstrumentation());

// API
builder.Services.AddControllers();
builder.Services.AddProblemDetails();  // RFC 7807 errors

var app = builder.Build();
app.MapControllers();
app.Run();
```

**Why:** Dependency graph is explicit and testable. Swapping IMcpClient becomes a one-line change.

### 6. Terraform Environment Pattern

Structure infrastructure for multi-environment promotion:

```
src/infra/terraform/
├── main.tf              # Resource definitions
├── variables.tf         # Input declarations
├── outputs.tf           # Values for CI/CD
├── locals.tf            # Helpers
├── environments/
│   ├── dev.tfvars       # Development overrides
│   ├── test.tfvars      # Test overrides
│   └── prod.tfvars      # Production overrides
```

**Example dev.tfvars:**
```hcl
name_prefix         = "banking-agent-dev"
location            = "eastus"
container_registry  = "bankintagentdev.azurecr.io"
orchestrator_image  = "bankintagentdev.azurecr.io/orchestrator:latest"
foundry_agent_urls  = ["https://foundry-dev.azurecontainerapps.io/agents/*"]
log_retention_days  = 7
enable_debug_logs   = true
```

**Why:** Keeps Terraform DRY; enables safe promotion from dev → test → prod without code changes.

## Key Takeaways

1. **Start with IMcpClient abstraction**, even if hardcoded at first. Enables testing and iteration.
2. **Audit events are domain-level objects**, not logging afterthoughts. They drive compliance.
3. **Correlation IDs flow through every layer**. OpenTelemetry Activity captures it.
4. **Managed identity from the start**. No service principal pivoting later.
5. **Layered architecture is non-negotiable for banking**. Domain logic survives infrastructure rewrites.

## When to Use This

- Building a C# agent orchestrator that coordinates multiple remote reasoning services
- Integrating MCP tools into a .NET application
- Ensuring audit trails and approval workflows in regulated domains (banking, healthcare)
- Multi-environment Terraform deployments with promotion safety

## When NOT to Use This

- Single-file scripts or quick prototypes (overkill)
- Fully autonomous agents without approval gates (not applicable to banking)
- Synchronous request-response patterns only (this scales to queued workflows too)

## References

- Project constitution: `docs/project-constitution.md`
- Technical spec: `docs/technical-spec.md`
- Example implementation: `src/orchestrator/` and `src/infra/terraform/`
- Decision inbox: `.squad/decisions/inbox/aria-kickoff-plan.md`

---

*Authored by Aria (Lead Architect). Last updated 2026-07-29.*
