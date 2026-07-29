# Phase Plan

## Phase 0 — Foundation and alignment
Objective: lock the architecture, contracts, and delivery guardrails before implementation begins.

Deliverables:
- Finalized architecture decision for the C# Agent Framework orchestrator and MCP-backed Foundry tools
- Initial backlog and implementation ownership
- Repo conventions for Entra-only auth, tracing, and deployment

Exit criteria:
- Constitution, functional spec, and technical spec all reflect the target architecture
- Squad roster and planning documents are in place

## Phase 1 — Orchestrator and workflow core
Objective: build the C# orchestrator skeleton and the core workflow state machine.

Deliverables:
- Versioned API surface for workflow intake and status lookup
- Workflow state model with correlation IDs and durable audit fields
- Approval gate abstraction for sensitive actions

Exit criteria:
- A request can be accepted, traced, and paused for approval
- The orchestrator can return structured responses and trace IDs

## Phase 2 — Minimal UI and workflow experience
Objective: add a simple user-facing experience so the workflow is easy to demo and test.

Deliverables:
- A minimal web UI for submitting a request, viewing workflow status, and approving sensitive actions
- Clear trace ID and workflow status display in the UI
- Basic error and approval-state feedback for the user

Exit criteria:
- A user can submit a request from the browser and see the workflow progress and approval state
- The UI is connected to the orchestrator API and the demo workflow is understandable end to end

## Phase 3 — MCP and Foundry tool integration
Objective: connect the orchestrator to Microsoft Foundry-hosted LangGraph agents through MCP.

Deliverables:
- MCP client abstraction and tool registry
- Tool wrappers for reasoning, planning, and action tasks
- Contract normalization for tool responses

Exit criteria:
- The orchestrator can invoke at least one Foundry-backed agent tool end to end
- Tool failures and timeouts are surfaced as structured errors

## Phase 4 — Safety, audit, and observability
Objective: make the workflow production-minded and reviewable.

Deliverables:
- Approval enforcement for sensitive actions
- Structured logging, telemetry, and trace emission
- ProblemDetails-based error responses and input validation

Exit criteria:
- Sensitive actions require explicit approval and produce an auditable trail
- Logs and traces can be correlated to a workflow run

## Phase 5 — Azure deployment and infrastructure
Objective: make the solution deployable to Azure with Terraform.

Deliverables:
- Terraform modules for Azure Container Apps, managed identity, and supporting resources
- HorizonDB placeholder deployment via AzAPI if needed
- Environment-specific configuration for dev/test/prod

Exit criteria:
- A team member can provision the baseline infrastructure with Terraform
- Containerized services can be deployed into Azure Container Apps

## Phase 6 — CI/CD and demo hardening
Objective: make the project repeatable and showcase-ready.

Deliverables:
- GitHub Actions workflow for build validation and deployment
- Demo data and a simple end-to-end scenario
- Security and reliability review

Exit criteria:
- CI passes for trunk builds and the demo flow works end to end
- The repo is ready for the next implementation sprint
