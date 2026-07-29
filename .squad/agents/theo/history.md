# Theo History

Project: langgraph-learnings
Stack: C#, .NET 8, ASP.NET Core, Azure Container Apps, Terraform, GitHub Actions, MCP, Microsoft Foundry
Owner: brian
Description: A banking agent prototype where a C# orchestrator uses Microsoft Agent Framework and MCP to invoke Foundry-hosted LangGraph agents.

## Learnings
- Added Azure Monitor OpenTelemetry wiring to the orchestrator with a startup guard so local development stays clean when APPLICATIONINSIGHTS_CONNECTION_STRING is unset.
- Tagged the active request Activity with `correlation_id` in CorrelationIdMiddleware so traces and structured logs share the same request correlation value.
