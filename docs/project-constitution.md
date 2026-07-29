# Project Constitution

## Purpose
Create a banking-focused agentic reference application in which a C# orchestrator agent built with Microsoft Agent Framework coordinates a workflow of LangGraph-hosted agents in Microsoft Foundry through MCP tools.

## Core objectives
- Build a C# orchestrator agent using Microsoft Agent Framework as the primary control plane.
- Use MCP to load Microsoft Foundry-hosted LangGraph agents as tools for specialized reasoning and action workflows.
- Deploy containerized services to Azure Container Apps with Entra ID-based authentication.
- Use Azure HorizonDB as the operational data store for workflow state and audit history.
- Use LiteLLM as the AI gateway for provider abstraction, retries, and routing where direct model access is needed.
- Provision infrastructure with Terraform and automate builds and deployments with GitHub Actions.
- Never use keys for authentication; always use Microsoft Entra ID.
- Make sensitive actions require explicit approval and produce detailed traces.

## Architectural principles
1. Security and identity: No API keys or shared secrets for service authentication. Entra ID, managed identity, and workload identity are mandatory.
2. Layered architecture: Keep domain logic independent from frameworks and infrastructure. Use a Domain → Application → Infrastructure → API structure.
3. Agentic design: Build multi-step workflows with explicit approvals, tool boundaries, and traceable decisions. The C# orchestrator agent uses Microsoft Agent Framework and loads Foundry-hosted LangGraph agents as MCP tools rather than embedding their logic directly.
4. Observability and traceability: Every workflow emits structured logs, OpenTelemetry traces, and correlation IDs. Never log secrets or PII.
5. Cloud-native delivery: Azure Container Apps, Terraform, and GitHub Actions are the default deployment path. Containers must be non-root and images must be built reproducibly.
6. API-first and versioned: Public APIs are versioned, validated at the boundary, and return ProblemDetails for failures.
7. Data discipline: Persist workflow state and audit metadata using parameterized access patterns, pagination on lists, and audit fields where applicable.

## Architectural guardrails
- C# is the primary application language for orchestration, policy enforcement, and API integration.
- The C# orchestrator agent uses Microsoft Agent Framework and loads Microsoft Foundry-hosted LangGraph agents as MCP tools.
- Communication between the orchestrator and remote agents should use MCP or versioned HTTP contracts where a non-MCP gateway is required.
- LiteLLM should sit in front of model providers to provide a unified gateway for direct model access or fallback paths.
- The orchestrator must enforce approval gates before any sensitive banking action.
- Every workflow step must emit traceable, structured audit data.
- The system must be deployable as Azure Container Apps with managed identity and secretless access where possible.

## Non-goals for v1
- Full production banking operations or live transaction execution against real bank systems.
- Advanced multi-tenant enterprise security controls beyond the initial approval and audit patterns.
- A fully autonomous agent that can act without guardrails.
- A deviation from Entra ID-only authentication without an approved architecture decision record.

## Document hierarchy and governance
- This constitution is the highest authority for this repository. Lower-level documents, plans, and implementation tasks must align with it.
- The expected decision order is: constitution → active spec → implementation plan → tasks → backlog → local judgment.
- Any change to this constitution requires a documented amendment and version bump.
- Before declaring work complete, run the local quality gate: build, tests, formatting, and targeted validation for changed services.
- Any deviation from Entra ID-only authentication, no-key policy, or agent framework guidance requires an architecture decision record before merge.

## Success criteria
- A user can submit a banking request and receive a guided multi-step workflow response.
- The system can distinguish between informational and sensitive actions.
- The workflow pauses for approval before executing any sensitive action.
- Infrastructure can be provisioned by Terraform from a clean environment.
- Logs and traces identify the request, agent decision, approval event, and final action state.
