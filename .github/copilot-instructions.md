# Copilot Instructions for the Banking Agent Prototype

## Project overview
This repository contains a reference banking agent application built around a .NET orchestrator, Python LangGraph/LangChain agents, Azure Container Apps deployment, and Terraform-based infrastructure. The implementation should reflect the principles in `docs/project-constitution.md` and the active specification in `docs/functional-spec.md`.

## Architecture
- Follow a layered structure: Domain → Application → Infrastructure → API/Web.
- Keep dependencies pointing inward. Domain code must not depend on HTTP, EF Core, or framework-specific packages.
- Keep controllers thin. Parse input, invoke an application service, and return a result.
- Prefer constructor injection and explicit composition in the application entrypoint.
- Expose versioned REST APIs under `/api/v1/...` and return ProblemDetails for failures.

## Build, test, and quality gate
- Use `dotnet build -c Release` for the .NET app.
- Use `dotnet test` when test projects exist.
- Use `dotnet format --verify-no-changes` when formatting is part of the workflow.
- Use `python -m compileall src/agents/python` for the Python service stubs until a dedicated test runner exists.
- Run the local quality gate before marking work complete.

## AI and agent guidance
- Use Microsoft Foundry and Microsoft Agent Framework for agentic orchestration where practical.
- Do not introduce Semantic Kernel for new work in this repository.
- Implement the primary orchestrator as a C# Agent Framework agent.
- Use Microsoft Foundry-hosted LangGraph agents as MCP-backed tools rather than embedding their logic directly in the orchestrator.
- Hosted agents call Microsoft Foundry models directly. Do not add an AI gateway without an ADR; see `docs/decisions/0001-remove-litellm-gateway.md`.
- Keep workflows multi-step, approval-controlled, and traceable.

## Security and identity
- Never use API keys for service authentication. Use Microsoft Entra ID, managed identity, and workload identity.
- Do not commit secrets or keys. Keep secrets in Azure Key Vault or secure runtime stores when unavoidable.
- Validate input at the boundary and avoid logging secrets or PII.
- Prefer non-root containers and secure defaults.

## Data and observability
- Persist workflow state and audit events in a structured store.
- Use correlation IDs and structured logging for every workflow step.
- Capture OpenTelemetry traces for agent calls, approval transitions, and persistence operations.

## Delivery
- Keep infrastructure definitions in `infrastructure/` (convention-over-configuration; `region` is the only Terraform input).
- Keep GitHub Actions workflows in `.github/workflows/`.
- Update `docs/` when requirements, architecture, or guardrails change.
