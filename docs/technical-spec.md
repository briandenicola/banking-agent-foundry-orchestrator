# Technical Specification

## Solution shape
A C# orchestrator agent coordinates a banking workflow and uses Microsoft Agent Framework to call LangGraph-hosted agents in Microsoft Foundry as MCP tools. Model access flows through LiteLLM where direct model access is needed; otherwise the Foundry-hosted agents remain the primary reasoning providers. The implementation should follow a layered structure: Domain → Application → Infrastructure → API/Web.

## Components
- C# orchestrator agent
  - Owns workflow state, approvals, correlation IDs, and API integration.
  - Exposes a versioned HTTP API for user requests and workflow updates.
  - Uses constructor injection and thin API handlers.
- MCP tool integration
  - Connects the orchestrator to Microsoft Foundry-hosted LangGraph agents as specialized tools.
  - Supports tool discovery, parameter passing, and response normalization.
- Foundry-hosted LangGraph agents
  - Provide reasoning, planning, and specialized action capabilities as remote workflow services.
- LiteLLM gateway
  - Centralizes model access for direct model calls or fallback paths.
  - Supports provider abstraction and future model fallback.
- Azure Container Apps
  - Hosts the orchestrator and any supporting gateway services as independently deployable services.
- Azure HorizonDB
  - Stores workflow state, approvals, and audit records.
  - Initial Terraform scaffolding should use AzAPI if the service is not yet exposed as a first-class AzureRM resource.
- Azure Monitor / Application Insights / OpenTelemetry
  - Collects logs, traces, and operational telemetry for each workflow run.

## Runtime flow
1. The user submits a request to the C# orchestrator.
2. The orchestrator uses Microsoft Agent Framework to plan the next step.
3. The orchestrator loads the appropriate MCP tools backed by Microsoft Foundry-hosted LangGraph agents.
4. If sensitive, the orchestrator pauses for explicit approval.
5. After approval, the orchestrator executes the bounded action and persists the audit trail.
6. Direct model calls, if any, are routed via LiteLLM.
7. The orchestrator returns a correlation ID and structured response to the caller.

## API and domain contract
- Public endpoints should be versioned and exposed under `/api/v1/...`.
- Controllers should be thin; business logic should live in application services and domain types.
- Request and response DTOs should be explicit and use immutable records where practical.
- Failed requests should return RFC 7807 ProblemDetails rather than raw exception text.

## Security and governance
- Use managed identity for Azure resource access where possible.
- Never use keys for authentication; use Microsoft Entra ID for service-to-service authentication.
- Keep secrets in Azure Key Vault or environment-based secret stores only when unavoidable.
- Enforce approval gates for all sensitive actions.
- Store complete trace metadata for each workflow step.
- Include GitHub Actions workflows for build validation and deployment automation.
- Containers should run as non-root and should avoid embedding secrets in build or runtime configuration.

## Observability and quality
- Emit structured logs with correlation IDs and a request trace ID on every workflow event.
- Capture OpenTelemetry spans for agent calls, workflow transitions, approvals, and persistence operations.
- Validate inputs at the API boundary and avoid logging PII or sensitive data.
- Keep the quality gate local and repeatable: build, tests, formatting, and targeted validation for changed services.

## Infrastructure plan
- Terraform provisions the Azure Container Apps environment, Container Apps, managed identities, HorizonDB resources, and supporting networking.
- Terraform should keep the deployment reproducible and environment-agnostic.
- The deployment should support separate environments for dev, test, and prod.
- CI/CD should use GitHub Actions and should deploy only after successful build validation.

## Proposed repo layout
- `/src/domain/` - domain models and policy rules
- `/src/application/` - workflow orchestration, use cases, and service contracts
- `/src/infrastructure/` - Azure, persistence, and external integration implementations
- `/src/api/` - versioned HTTP endpoints and DTOs
- `/src/agents/python/` - Python LangGraph/LangChain agents
- `/infrastructure/` - Terraform configuration (convention-over-configuration; `region` is the only input)
- `/docs/` - specifications and architecture notes
