# Squad Decisions

## Active Decisions

- **2026-07-29:** The project will use a C# orchestrator agent built with Microsoft Agent Framework and MCP to invoke Microsoft Foundry-hosted LangGraph agents as tools.
- **2026-07-29:** Authentication will use Microsoft Entra ID and managed identity; API keys are not allowed for service authentication.
- **2026-07-29:** Azure Container Apps, Terraform, and GitHub Actions are the default deployment path for the reference implementation.
- **2026-07-29:** Phase 1 scope focuses on orchestrator scaffolding, hardcoded MCP tool registry, starter Terraform, and GitHub Actions validation (no container push/deployment yet).
- **2026-07-29:** Layered architecture enforced: Domain → Application → Infrastructure → API; all state transitions emit structured audit trails with correlation IDs.

## Governance

- All meaningful changes require team consensus
- Document architectural decisions here
- Keep history focused on work, decisions focused on direction
