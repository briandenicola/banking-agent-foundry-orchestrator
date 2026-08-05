# Phase Plan

This plan reflects the current state of the repository after the initial
implementation and documentation pass. The work is now centered on hardening the
platform pattern and turning it into a reusable lab and reference architecture.

## Status legend
- Completed: implemented and validated in the repository.
- In progress: partially implemented and ready for follow-on hardening.
- Planned: still needed for broader adoption.

## Phase 0 — Foundation and alignment (Completed)
Objective: lock the architecture, contracts, and delivery guardrails before implementation begins.

Delivered:
- Architecture and governance docs for the C# orchestrator, hosted agents, and Azure deployment
- A backlog and issue-driven implementation path
- Repository conventions for approvals, tracing, and deployment behavior

## Phase 1 — Orchestrator and workflow core (Completed)
Objective: build the C# orchestrator skeleton and the core workflow state machine.

Delivered:
- Versioned workflow API surface for intake and status lookup
- Durable workflow state with correlation IDs, event persistence, and approval transitions
- A workflow execution path that can pause for approval and recover safely

## Phase 2 — Minimal UI and workflow experience (Completed)
Objective: add a simple user-facing experience so the workflow is easy to demo and test.

Delivered:
- A minimal web UI for submitting requests, viewing workflow progress, and approving sensitive actions
- Trace ID and workflow status visibility in the UI
- Basic workflow-state and approval feedback

## Phase 3 — Foundry and hosted-agent integration (Completed with follow-on hardening)
Objective: connect the orchestrator to Microsoft Foundry-hosted agents through a typed transport boundary.

Delivered:
- A typed Foundry-backed invocation boundary and hosted-agent request/response contract
- Planner-to-specialist context handoff and execution-mode reporting
- A deployable hosted-agent packaging path for Azure

Follow-on work:
- Continue tightening the standards-compliant MCP boundary and runtime discovery path
- Add more explicit failure handling and contract versioning across the tool boundary

## Phase 4 — Safety, audit, and observability (Completed)
Objective: make the workflow production-minded and reviewable.

Delivered:
- Approval enforcement for sensitive actions
- Structured logging, telemetry, and correlation support for workflow runs
- ProblemDetails-based error responses and input validation

## Phase 5 — Azure deployment and infrastructure (Completed)
Objective: make the solution deployable to Azure with Terraform and Container Apps.

Delivered:
- Terraform assets for Container Apps, managed identity, PostgreSQL, and supporting resources
- A deployable app image path through Azure Container Registry
- Verified deployment and migration flow for the sample environment

## Phase 6 — Education, platform hardening, and adoption (In progress)
Objective: turn the sample into a reusable lab and operational reference for Azure platform engineers.

Planned deliverables:
- A hosted-agents lab tailored to Azure platform engineering concerns
- Platform-focused documentation for identity, RBAC, networking, and observability
- Additional hardening for environment separation, policy, and operations readiness

Next steps:
- Add more platform-specific exercises around private networking and role boundaries
- Expand the lab to cover multi-environment deployment and operational runbooks
- Continue refining the MCP and hosted-agent contract for broader reuse
