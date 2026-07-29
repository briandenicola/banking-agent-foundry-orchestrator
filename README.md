# Banking Agent Prototype

This repository contains a starter implementation for a banking-focused agentic application built around:

- C# orchestrator agent using Microsoft Agent Framework
- Microsoft Foundry-hosted LangGraph agents exposed as MCP tools
- LiteLLM as the AI gateway for any direct model access
- Azure Container Apps deployment
- Terraform-based infrastructure

## Repository layout
- `src/orchestrator/` - C# web API orchestrator and Agent Framework entrypoint
- `src/infra/terraform/` - Terraform for Azure resources
- `docs/` - project constitution and specifications

## Getting started
1. Restore the solution: `dotnet restore banking-agent.sln`
2. Build the solution: `dotnet build banking-agent.sln`
3. Run the orchestrator: `dotnet run --project src/orchestrator/orchestrator.csproj`
4. Run the UI: `dotnet run --project src/webui/webui.csproj`
5. Build container images locally with `docker build -f src/orchestrator/Dockerfile -t banking-agent-orchestrator:test .` and `docker build -f src/webui/Dockerfile -t banking-agent-webui:test .`
6. Provision infrastructure with Terraform from `src/infra/terraform/environments/dev`
7. Use GitHub Actions in `.github/workflows/build-and-deploy.yml` for build validation and deployment automation.

## Implementation planning
- `docs/phase-plan.md` outlines the implementation phases for the project.
- `docs/implementation-backlog.md` captures the near-term backlog and acceptance criteria.
- `docs/first-sprint-plan.md` describes the first implementation sprint and its scope.
