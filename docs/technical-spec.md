# Technical Specification

## Solution shape
A .NET orchestrator service coordinates a banking workflow, while Python services host LangGraph/LangChain agents. Model access flows through LiteLLM, which acts as a gateway for provider routing, retries, and consistent request handling.

## Components
- C# orchestrator service
  - Owns workflow state, approvals, correlation IDs, and API integration.
  - Exposes an HTTP API for user requests and workflow updates.
- Python agent services
  - Agent A: intent understanding and request normalization.
  - Agent B: action planning and safety classification.
  - Communicate with the orchestrator over HTTP or MCP.
- LiteLLM gateway
  - Centralizes model access for the Python agents and any C#-based inference clients.
  - Supports provider abstraction and future model fallback.
- Azure Container Apps
  - Hosts the orchestrator, agent services, and LiteLLM gateway as independently deployable services.
- Azure HorizonDB
  - Stores workflow state, approvals, and audit records.
  - Initial Terraform scaffolding should use AzAPI if the service is not yet exposed as a first-class AzureRM resource.
- Azure Monitor / Application Insights
  - Collects logs, traces, and operational telemetry.

## Runtime flow
1. The user submits a request to the C# orchestrator.
2. The orchestrator calls the reasoning agent.
3. The planning agent evaluates whether the request is informational or sensitive.
4. If sensitive, the orchestrator pauses for explicit approval.
5. After approval, the orchestrator executes the bounded action and persists the audit trail.
6. All model calls are routed via LiteLLM.

## Security and governance
- Use managed identity for Azure resource access where possible.
- Never use keys for authentication; use Microsoft Entra ID for service-to-service authentication.
- Keep secrets in Azure Key Vault or environment-based secret stores only when unavoidable.
- Enforce approval gates for all sensitive actions.
- Store complete trace metadata for each workflow step.
- Include GitHub Actions workflows for build validation and deployment automation.

## Infrastructure plan
- Terraform provisions the Azure Container Apps environment, Container Apps, managed identities, HorizonDB resources, and supporting networking.
- Terraform should keep the deployment reproducible and environment-agnostic.
- The deployment should support separate environments for dev, test, and prod.

## Proposed repo layout
- /src/orchestrator/ (C#)
- /src/agents/python/ (Python LangGraph/LangChain agents)
- /src/infra/terraform/ (Terraform modules and environment configs)
- /docs/ (specifications and architecture notes)
