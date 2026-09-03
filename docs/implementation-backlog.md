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
    - **Answered.** [`verify-memory-scope.py`](../scripts/verify-memory-scope.py) was run against a live project. Foundry ignores a scope passed next to an `agent_reference` — it accepts the field, returns `200`, and both scopes read the same fact — but honours the scope when the agent definition is sent inline, where a second scope reads nothing. `CustomerProfileClient` therefore sends scoped turns inline. The returned memories also carry their real stored scope, `<objectId>_<tenantId>` of the caller, which is why `EnforceScope` was effective rather than merely defensive.
    - The write path was the other half, and it was missing: `/Profile` sent turns unscoped while the workflow read a customer scope, so nothing ever wrote where the workflow looked. Both now carry the signed-in customer. This class of bug is silent — the write succeeds and simply lands where nothing reads — so it is pinned by `CustomerProfileEndpointScopeTests`.
    - Acceptance criteria: a preference stated in one Web UI session changes the response in a later, separate session for the same user and only that user, and the call is visible in Application Insights.

15. P2 — Define a banking Responsible AI policy as code
    - The deployer supports attaching a content safety guardrail to the `customer-profile` prompt agent, but no policy is configured, so `rai_config` is omitted and the agent relies on the model deployment's own default content filter.
    - Sending an empty `rai_config` to select the platform default does **not** work, despite the published guidance saying it does. Api-version `v1` rejects it with `invalid_payload`: "Required property 'rai_policy_name' is missing", and the failure takes down the whole agent deploy. `test_memory_agent_omits_rai_config_when_no_policy_is_configured` pins this. A policy ARM resource ID is therefore mandatory, not optional, which makes this item a prerequisite for any agent-boundary guardrail rather than a refinement.
    - Create a `Microsoft.CognitiveServices/accounts/raiPolicies` resource on the Foundry account in [`ai.tf`](../infrastructure/ai.tf) with thresholds set deliberately for a banking assistant, expose its ARM resource ID as a Terraform output, and pass it to the deployer as `MEMORY_AGENT_RAI_POLICY`. The deployer already reads that variable and sends it as `rai_config.rai_policy_name`, so this is Terraform and wiring only.
    - Note that every api-version for this resource type is preview (`2025-10-01-preview` is the current default), which is why it was kept off the critical path rather than added during an end-to-end deployment.
    - Consider whether the same policy should be attached to the four hosted agents, which accept the identical `rai_config` field on their own definitions.
    - Acceptance criteria: `GET /agents/customer-profile/versions/{n}` returns the bank's own policy ARM ID in `definition.rai_config.rai_policy_name`, and a prompt the policy is configured to block returns HTTP 400 with a `content_filter` error rather than a model response.

16. P2 — Propagate the signed-in user with an on-behalf-of flow
    - **Delivered behind `enable_obo`, off by default.** The exchange runs between the Web UI and the orchestrator; see [ADR 0005](decisions/0005-on-behalf-of-client-secret.md) for the credential decision and the operations guide for setup. What follows records why the shape is what it is.
    - Before this, the Web UI read the platform-verified `X-MS-CLIENT-PRINCIPAL-ID` header and passed that object ID to the orchestrator as a *value*, which the orchestrator asserted as the memory scope while calling Foundry as its own managed identity. It was identity propagation by assertion, not by token. The orchestrator trusted the caller to state who the customer is, which is only sound because the orchestrator has internal ingress and the Web UI is the sole caller. That remains the behaviour with the flag off.
    - **This was never needed for per-customer memory.** Inline scoping already isolates memory per customer, verified by [`verify-memory-scope.py`](../scripts/verify-memory-scope.py) and by the absence of the `EnforceScope` mismatch warning in a live run. The case for OBO is the trust model: an exchanged token lets the orchestrator verify the customer rather than take the Web UI's word for it, and makes the identity chain demonstrable rather than asserted.
    - Feasible half: OBO between the Web UI and the orchestrator. Both applications can be registered in a tenant the operator controls, so the `api://` identifier URI that issue #30 is blocked on is available there. Note this does **not** also unblock `enable_service_auth`, which acquires its token from a managed identity — a managed identity can only obtain tokens from its own home tenant, so it cannot get one for an application registered elsewhere. OBO sidesteps that only because it uses the application's own credential rather than the managed identity.
    - Infeasible half: OBO onward to Foundry. Its data plane authorises on Azure RBAC, so a delegated token is evaluated against the *user* principal and requires that user to exist in the deployment's tenant with a Foundry role. Real banking customers are not principals in the bank's Foundry tenant, so this is the wrong model regardless of whether the tenant permits it.
    - Architectural limit worth recording: workflows resume in the background through the recovery worker, long after a user token has expired. OBO covers the interactive path only. Persisting refresh tokens to bridge that gap was rejected — see the header of [`apps/webui-auth.tf`](../apps/webui-auth.tf) — because it puts user credentials at rest in the workflow store.
    - Remaining: the confidential-client secret and the token store's blob SAS are both keys at rest. ADR 0005 accepts them and records the condition under which they should go — a federated identity credential becomes viable once the registration and the managed identity can live in the same tenant, which is issue #30.
    - Acceptance criteria: met with `enable_obo = true`. The orchestrator derives the customer from a validated token rather than a request field (`CustomerAssertionGuard`), a request asserting a different customer than the token carries is rejected with 403 (`CustomerAssertionGuardTests`), and the background workflow path continues to work without any user token because the recovery worker never passes through the guard.
