# Aria History

Project: langgraph-learnings
Stack: C#, .NET 8, Azure Container Apps, Terraform, GitHub Actions, MCP, Microsoft Foundry
Owner: brian
Description: A banking agent prototype where a C# orchestrator uses Microsoft Agent Framework and MCP to invoke Foundry-hosted LangGraph agents.

## Phase 1 Kickoff (2026-07-29)

### Key Decisions
1. **MCP abstraction first:** `IMcpClient` interface enables testing and swappability. Hardcoded tool registry in Phase 1; Foundry dynamic discovery deferred.
2. **Layered architecture enforcement:** Domain models (WorkflowState, ApprovalGate, AuditEvent) live in domain layer; orchestration logic in application layer; MCP integration in infrastructure layer.
3. **No secrets in Terraform:** Managed identity only; role assignments via Terraform; Key Vault integration deferred.
4. **Environment-agnostic tfvars pattern:** Separate dev.tfvars, test.tfvars, prod.tfvars for promotion safety.
5. **CI/CD staged validation:** Phase 1 validates builds and formatting only; container pushes and Terraform apply deferred to Phase 2.

### Critical Patterns
- **Correlation ID on every workflow step:** TraceId UUID generated on POST /api/v1/workflow; threaded through all MCP calls and audit events.
- **Audit events as first-class domain objects:** Every state transition (received → processing → approved → executed/failed) emits structured AuditEvent with timestamp, action, result, actor.
- **Thin controllers, thick domain:** API handlers delegate to `WorkflowOrchestrationService` immediately; business logic owns state machine and guardrails.
- **Error contracts:** Always return RFC 7807 ProblemDetails; never expose raw exception stacks to callers.

### File Locations to Reference
- Constitution: `docs/project-constitution.md` (source of truth for guardrails)
- Technical spec: `docs/technical-spec.md` (component contracts and runtime flow)
- Terraform base: `src/infra/terraform/main.tf` (placeholder resource pattern)
- Orchestrator stub: `src/orchestrator/Program.cs` (Minimal Apis base; replace with structured service injection in Phase 1)
- CI/CD base: `.github/workflows/build-and-deploy.yml` (currently placeholder; add validation steps in Phase 1)

### Learnings
- Constitution emphasizes "no keys ever" harder than typical projects; this shapes managed identity design from day 1.
- Approval gates before sensitive actions are non-negotiable; this affects workflow state machine and API contracts early.
- Audit trails must be structured from the start; unstructured logs won't meet banking compliance expectations later.
- Foundry agent integration is deferred strategically; Phase 1 proves the orchestrator and MCP contract with stubs.

### Future Attention
- LiteLLM routing (Phase 2): When does orchestrator call LiteLLM vs. Foundry agents directly? Document decision in ADR.
- HorizonDB schema (Phase 2): Exact storage of WorkflowState and AuditEvent; pagination and query patterns.
- Multi-agent coordination (Phase 2+): How do multiple MCP tools orchestrate? Dependency ordering? Branching workflows?
