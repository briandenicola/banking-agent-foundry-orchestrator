# Implementation Backlog

Delivery status below is reconciled against GitHub Issues. The issue-by-issue
close plan in [issue-close-action-plan.md](./issue-close-action-plan.md) covered
issues #17, #18, and #20, all of which are now closed; it is kept as history.

## Priority legend
- P0: must-have for the first implementation milestone
- P1: important for a usable prototype
- P2: follow-up hardening or demo work

## Backlog

1. P0 — Define the orchestrator contract and workflow state model
   - Define the request/response contract, correlation ID, workflow state, and approval lifecycle.
   - Acceptance criteria: the orchestrator can accept a request and return a traceable workflow state.
   - Delivered; workflow contract, state model, and approval lifecycle shipped with GitHub issues #5 and #7.

2. P0 — Implement the C# Agent Framework orchestrator skeleton
   - Create the initial orchestrator service, thin API endpoints, and dependency injection composition.
   - Acceptance criteria: the app starts and serves a versioned health and workflow endpoint.
   - Delivered; Agent Framework orchestration completed in GitHub issue #17.

3. P0 — Build a minimal web UI for workflow interaction
   - Create a simple Razor or Blazor experience for entering requests, viewing workflow status, and approving sensitive actions.
   - Acceptance criteria: a user can submit a request from the browser and see the workflow state and approval guidance.
   - Delivered; the Web UI was rebuilt with durable progress feedback in GitHub issue #16.

4. P0 — Build the MCP client and tool registry
   - Create the abstraction for loading MCP tools and a generic registry for Foundry-backed agents.
   - Acceptance criteria: the orchestrator can discover a registered MCP tool and invoke it.
   - Delivered; real MCP discovery and invocation completed in GitHub issues #18 and #36.

5. P0 — Integrate a first Foundry-backed LangGraph tool
   - Wire one reasoning or planning tool from Microsoft Foundry-hosted LangGraph agents into the orchestrator.
   - Acceptance criteria: a request produces a tool-backed response with structured metadata.
   - Delivered; all four hosted agents are invoked over MCP per GitHub issues #18 and #36.

6. P0 — Implement approval enforcement for sensitive actions
   - Add policy-driven approval gates before dispute initiation or other high-risk operations.
   - Acceptance criteria: sensitive actions cannot execute without explicit approval.
   - Delivered; approval gating and transactional execution completed in GitHub issue #4.

7. P0 — Add structured logging, tracing, and error handling
   - Emit structured logs, correlation IDs, and OpenTelemetry spans for workflow steps and tool calls.
   - Acceptance criteria: each workflow run can be traced and diagnostics are captured without secrets or PII.
   - Delivered; observability completed in GitHub issue #6 and orchestrator logging in issue #26.

8. P1 — Provision Azure Container Apps and supporting infrastructure with Terraform
   - Create Terraform modules for Container Apps, managed identity, and supporting network/observability resources.
   - Acceptance criteria: infrastructure can be provisioned for a dev environment from a clean machine.
   - Delivered; Taskfile automation repaired in GitHub issue #11 and private networking added in issue #27.

9. P1 — Add GitHub Actions for build and deploy validation
   - Build, test, and Terraform validation on pull requests; deployment on main after review.
   - Acceptance criteria: CI validates the repo and deployment steps are documented.
   - Delivered; CI and deployment pipelines modernized in GitHub issue #10.

10. P1 — Add a demo scenario and sample banking data
    - Provide a small dataset and a realistic transaction-explanation or dispute-approval flow.
    - Acceptance criteria: a user can run through the main happy path locally or in a dev environment.
   - Delivered; demo scenarios and non-PII data added in GitHub issue #8.

11. P2 — Harden for security and reliability
    - Review secrets handling, identity configuration, retry behavior, and operational readiness.
    - Acceptance criteria: the implementation meets the project constitution for security and observability.
    - Partially delivered. Private networking (#27), recovery attempt limits (#28), evidence retention (#29), Data Protection key persistence (#31), and model cost controls (#33) are closed. The Web UI is still public and unauthenticated; [#40](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/40) tracks the remainder.

12. P1 — Write a code-level technical implementation guide
    - Document the end-to-end request lifecycle from the web/API boundary through the C# orchestrator, planner agent, specialist agents, approval transitions, persistence, and final response.
    - Explain each agent's LangGraph state, nodes, routing decisions, model invocation, typed request/result contracts, local fallback behavior, and Microsoft Foundry Hosted Agent adapter.
    - Document how the four Hosted Agents are packaged and deployed independently, how their endpoints and managed identities differ, and how the orchestrator selects and invokes them.
    - Cover Microsoft Entra authentication, managed identity token acquisition, PostgreSQL access, Application Insights/OpenTelemetry correlation, configuration, and failure handling.
    - Include sequence diagrams and direct references for every important implementation step so readers can move between the guide and the exact code.
    - Reference files and symbol names, not `path/to/file:line`. Line anchors were tried in [`agent-implementation.md`](./agent-implementation.md) and every one of them had drifted to unrelated code within a few releases; they were removed rather than re-pinned.
    - Acceptance criteria: a developer unfamiliar with the repository can trace the primary informational, suspicious-activity, and dispute-approval flows from entrypoint to completion using the guide and its verified code references.
    - Partially delivered. [`mvp-implementation-operations-guide.md`](./mvp-implementation-operations-guide.md) and [`agent-implementation.md`](./agent-implementation.md) cover the narrative and link to the relevant files and symbols. Sequence diagrams are still missing.

13. P1 — Audit all documentation for accuracy and currency
    - Review every file under `docs/` (plus `README.md` and `.github/copilot-instructions.md`) against the current implementation, Terraform stacks, Taskfile targets, and CI workflow.
    - Verify each documented command, environment variable, Terraform input/output, and `path/to/file:line` reference actually resolves and behaves as described.
    - Reconcile the backlog, phase plan, and issue-close action plan with the issues that have actually been delivered, and remove or mark superseded guidance.
    - Confirm ADRs in `docs/decisions/` still reflect the guardrails in force (no AI gateway, no Semantic Kernel, no API keys for service auth).
    - Acceptance criteria: a reader following any documented runbook end to end hits no stale command, variable, or code reference, and every doc states its current status.

14. P2 — Use the `customer-profile` prompt agent in the product
    - Mostly delivered. A `profile` step now runs ahead of the planner in [`AgentFrameworkWorkflowOrchestrator`](../src/application/AgentFrameworkWorkflowOrchestrator.cs) (`ExecuteProfileStepAsync`), and the preferences it recalls are passed to the planner and into the specialist's `context` dictionary. The step is fail-open: a workflow still completes when the profile agent is undeployed, unreachable, or scoped to somebody else, because losing personalisation is a better outcome than refusing a banking request.
    - The identity problem is addressed rather than solved. `WorkflowState.CustomerId` carries the signed-in user's object identifier from the Web UI into the background worker, because workflows execute long after the submitting request has ended and a user token would not survive the gap. Persisting user tokens to bridge that gap was rejected: it would put user credentials at rest in the workflow store.
    - Because the orchestrator still calls Foundry as its own managed identity, the scope is *asserted* by the application rather than derived from a user token. `CustomerProfileClient.EnforceScope` discards any memory returned under a different scope than the one requested, so a scope the service accepts and silently ignores costs personalisation instead of leaking one customer's details into another customer's workflow.
    - **Still open:** confirm against a live project that Foundry honours the requested scope at all. Run [`verify-memory-scope.py`](../scripts/verify-memory-scope.py), which writes a fact under one scope and requires that a second scope cannot read it. If no strategy isolates, per-customer memory is unavailable and the feature degrades to a single shared scope.
    - Acceptance criteria: a preference stated in one Web UI session changes the response in a later, separate session for the same user and only that user, and the call is visible in Application Insights.
