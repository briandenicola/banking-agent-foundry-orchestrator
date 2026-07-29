# Project Constitution

## Purpose
Create a banking-focused agentic application that demonstrates multi-step reasoning, approval-controlled actions, and production-minded observability. The first implementation will be a reference architecture for a .NET-based orchestrator that coordinates Python-based LangGraph/LangChain agents.

## Core objectives
- Learn LangGraph/LangChain concepts through a real-world banking scenario.
- Use C# for the orchestrator, workflow state, approvals, and enterprise integration.
- Host the solution in Azure using Azure Container Apps.
- Use Azure HorizonDB as the primary operational data store for workflow state and audit logs.
- Use LiteLLM as the AI gateway for model routing, retries, and provider abstraction.
- Provision all infrastructure with Terraform.
- Make sensitive actions require explicit approval and produce detailed traces.
- Never use keys for authentication; always use Microsoft Entra ID.
- Include GitHub workflows for build and deployment.

## Architectural guardrails
- C# is the primary application language for orchestration, policy enforcement, and API integration.
- Python hosts the LangGraph/LangChain agents and keeps the agent graph logic isolated from the .NET orchestrator.
- Communication between the orchestrator and agents should use a stable contract such as HTTP or MCP.
- LiteLLM should sit in front of model providers to provide a unified gateway for the agents and orchestrator.
- The orchestrator must enforce approval gates before any sensitive banking action.
- Every workflow step must emit traceable, structured audit data.
- The system must be deployable as Azure Container Apps with managed identity and secretless access where possible.

## Non-goals for v1
- Full production banking operations or live transaction execution against real bank systems.
- Advanced multi-tenant enterprise security controls beyond the initial approval and audit patterns.
- A fully autonomous agent that can act without guardrails.

## Design principles
1. Safety first: sensitive actions require explicit approval.
2. Traceability: every request, decision, and action is logged with correlation IDs.
3. Separation of concerns: Python handles agent reasoning; C# handles workflow control.
4. Cloud-native deployment: Azure Container Apps and Terraform are the default implementation path.
5. Build for learning and iteration: the first release should be understandable and easy to evolve.

## Success criteria
- A user can submit a banking request and receive a guided multi-step workflow response.
- The system can distinguish between informational and sensitive actions.
- The workflow pauses for approval before executing any sensitive action.
- Infrastructure can be provisioned by Terraform from a clean environment.
- Logs and traces identify the request, agent decision, approval event, and final action state.
