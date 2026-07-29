# Implementation Backlog

## Priority legend
- P0: must-have for the first implementation milestone
- P1: important for a usable prototype
- P2: follow-up hardening or demo work

## Backlog

1. P0 — Define the orchestrator contract and workflow state model
   - Define the request/response contract, correlation ID, workflow state, and approval lifecycle.
   - Acceptance criteria: the orchestrator can accept a request and return a traceable workflow state.

2. P0 — Implement the C# Agent Framework orchestrator skeleton
   - Create the initial orchestrator service, thin API endpoints, and dependency injection composition.
   - Acceptance criteria: the app starts and serves a versioned health and workflow endpoint.

3. P0 — Build a minimal web UI for workflow interaction
   - Create a simple Razor or Blazor experience for entering requests, viewing workflow status, and approving sensitive actions.
   - Acceptance criteria: a user can submit a request from the browser and see the workflow state and approval guidance.

4. P0 — Build the MCP client and tool registry
   - Create the abstraction for loading MCP tools and a generic registry for Foundry-backed agents.
   - Acceptance criteria: the orchestrator can discover a registered MCP tool and invoke it.

5. P0 — Integrate a first Foundry-backed LangGraph tool
   - Wire one reasoning or planning tool from Microsoft Foundry-hosted LangGraph agents into the orchestrator.
   - Acceptance criteria: a request produces a tool-backed response with structured metadata.

6. P0 — Implement approval enforcement for sensitive actions
   - Add policy-driven approval gates before dispute initiation or other high-risk operations.
   - Acceptance criteria: sensitive actions cannot execute without explicit approval.

7. P0 — Add structured logging, tracing, and error handling
   - Emit structured logs, correlation IDs, and OpenTelemetry spans for workflow steps and tool calls.
   - Acceptance criteria: each workflow run can be traced and diagnostics are captured without secrets or PII.

8. P1 — Provision Azure Container Apps and supporting infrastructure with Terraform
   - Create Terraform modules for Container Apps, managed identity, and supporting network/observability resources.
   - Acceptance criteria: infrastructure can be provisioned for a dev environment from a clean machine.

9. P1 — Add GitHub Actions for build and deploy validation
   - Build, test, and Terraform validation on pull requests; deployment on main after review.
   - Acceptance criteria: CI validates the repo and deployment steps are documented.

10. P1 — Add a demo scenario and sample banking data
    - Provide a small dataset and a realistic transaction-explanation or dispute-approval flow.
    - Acceptance criteria: a user can run through the main happy path locally or in a dev environment.

11. P2 — Harden for security and reliability
    - Review secrets handling, identity configuration, retry behavior, and operational readiness.
    - Acceptance criteria: the implementation meets the project constitution for security and observability.

12. P1 — Write a code-level technical implementation guide
    - Document the end-to-end request lifecycle from the web/API boundary through the C# orchestrator, planner agent, specialist agents, approval transitions, persistence, and final response.
    - Explain each agent's LangGraph state, nodes, routing decisions, model invocation, typed request/result contracts, local fallback behavior, and Microsoft Foundry Hosted Agent adapter.
    - Document how the four Hosted Agents are packaged and deployed independently, how their endpoints and managed identities differ, and how the orchestrator selects and invokes them.
    - Cover Microsoft Entra authentication, managed identity token acquisition, PostgreSQL access, Application Insights/OpenTelemetry correlation, configuration, and failure handling.
    - Include sequence diagrams and direct `path/to/file:line` references for every important implementation step so readers can move between the guide and the exact code.
    - Keep line references current whenever referenced code changes.
    - Acceptance criteria: a developer unfamiliar with the repository can trace the primary informational, suspicious-activity, and dispute-approval flows from entrypoint to completion using the guide and its verified code references.
