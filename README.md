# Banking Agent Prototype

This repository contains a starter implementation for a banking-focused agentic application built around:

- C# orchestrator service
- Python LangGraph/LangChain agents
- LiteLLM as the AI gateway
- Azure Container Apps deployment
- Terraform-based infrastructure

## Repository layout
- `src/orchestrator/` - C# web API orchestrator
- `src/agents/python/` - Python FastAPI agent services and dependencies
- `src/infra/terraform/` - Terraform for Azure resources
- `docs/` - project constitution and specifications

## Getting started
1. Restore the .NET app: `dotnet restore src/orchestrator/orchestrator.csproj`
2. Run the orchestrator: `dotnet run --project src/orchestrator/orchestrator.csproj`
3. Run the Python agents: `uvicorn app.main:app --host 0.0.0.0 --port 8000 --app-dir src/agents/python`
4. Provision infrastructure with Terraform from `src/infra/terraform/environments/dev`
5. Use GitHub Actions in `.github/workflows/build-and-deploy.yml` for build validation and deployment automation.
