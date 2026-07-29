# Banking Agent Prototype

This repository contains a starter implementation for a banking-focused agentic application built around:

- C# orchestrator agent using Microsoft Agent Framework
- Microsoft Foundry-hosted LangGraph agents exposed as MCP tools
- LiteLLM as the AI gateway for any direct model access
- Azure Container Apps deployment
- Terraform-based infrastructure

## Repository layout
- `src/orchestrator/` - C# web API orchestrator and Agent Framework entrypoint
- `infrastructure/` - Terraform for Azure resources (convention-over-configuration; `region` is the only input)
- `docs/` - project constitution and specifications

## Getting started
1. Copy `.env.example` to `.env` if you want to use the Task-based workflow shortcuts.
2. Build the solution: `task local:build`
3. Run the orchestrator: `task local:run`
4. Run the UI: `task local:ui`
5. Build container images locally with `task local:docker:build`
6. Configure Foundry connectivity by setting `FOUNDRY_AGENT_ENDPOINT` (and optionally `FOUNDRY_AGENT_NAME`, `FOUNDRY_SCOPE`, and `FOUNDRY_TOOL_ENDPOINTS`) before launching the orchestrator. When the endpoint is unset, the adapter returns a local fallback response so the app can still build and run.
7. Provision infrastructure with Terraform from `infrastructure/` using `task up` (only `region` is configurable, e.g. `task up -- westus2`; `task cloud:up` also works)
8. Tear down the environment with `task down` (or `task cloud:down`)
9. Use GitHub Actions in `.github/workflows/build-and-deploy.yml` for build validation and deployment automation.

## Implementation planning
- `docs/phase-plan.md` outlines the implementation phases for the project.
- `docs/implementation-backlog.md` captures the near-term backlog and acceptance criteria.
- `docs/first-sprint-plan.md` describes the first implementation sprint and its scope.
