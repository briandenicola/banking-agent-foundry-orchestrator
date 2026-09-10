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

11. P2 — Harden for security and reliability — **Delivered**
    - Review secrets handling, identity configuration, retry behavior, and operational readiness.
    - Acceptance criteria: the implementation meets the project constitution for security and observability.
    - Delivered. Private networking (#27), recovery attempt limits (#28), evidence retention (#29), Data Protection key persistence (#31), and model cost controls (#33) are closed. Web UI authentication ([#40](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/40)) is now in place: the Web UI signs the person in against a bring-your-own Entra app registration supplied as `webui_auth_client_id`, consumed either by Container Apps built-in authentication or by the application's own OpenID Connect handler — see [`apps/webui-auth.tf`](../apps/webui-auth.tf). The registration is hand-created because the target tenant denies the app registration Terraform would need, which is the same constraint behind `enable_service_auth = false`.

12. P1 — Write a code-level technical implementation guide — **Delivered**
    - Document the end-to-end request lifecycle from the web/API boundary through the C# orchestrator, planner agent, specialist agents, approval transitions, persistence, and final response.
    - Explain each agent's LangGraph state, nodes, routing decisions, model invocation, typed request/result contracts, local fallback behavior, and Microsoft Foundry Hosted Agent adapter.
    - Document how the four Hosted Agents are packaged and deployed independently, how their endpoints and managed identities differ, and how the orchestrator selects and invokes them.
    - Cover Microsoft Entra authentication, managed identity token acquisition, PostgreSQL access, Application Insights/OpenTelemetry correlation, configuration, and failure handling.
    - Include sequence diagrams and direct references for every important implementation step so readers can move between the guide and the exact code.
    - Reference files and symbol names, not `path/to/file:line`. Line anchors were tried in [`agent-implementation.md`](./agent-implementation.md) and every one of them had drifted to unrelated code within a few releases; they were removed rather than re-pinned.
    - Acceptance criteria: a developer unfamiliar with the repository can trace the primary informational, suspicious-activity, and dispute-approval flows from entrypoint to completion using the guide and its verified code references.
    - Delivered. [`mvp-implementation-operations-guide.md`](./mvp-implementation-operations-guide.md) and [`agent-implementation.md`](./agent-implementation.md) cover the narrative and link to the relevant files and symbols. Section 6 of the operations guide now carries all three named flows: informational (`transaction.explain`, completes without approval), suspicious activity (`suspicious.assess`, escalating to approval when a sensitive action term is present), and dispute approval (`dispute.plan`, always gated), alongside the existing rejection/concurrency and crash-recovery diagrams. The profile step and `AgentFrameworkWorkflowOrchestrator` appear in every diagram that starts at workflow creation, and a route table maps each flow to the `WorkflowRoutingPolicy` terms that select it.

13. P1 — Audit all documentation for accuracy and currency
    - Review every file under `docs/` (plus `README.md` and `.github/copilot-instructions.md`) against the current implementation, Terraform stacks, Taskfile targets, and CI workflow.
    - Verify each documented command, environment variable, Terraform input/output, and `path/to/file:line` reference actually resolves and behaves as described.
    - Reconcile the backlog, phase plan, and issue-close action plan with the issues that have actually been delivered, and remove or mark superseded guidance.
    - Confirm ADRs in `docs/decisions/` still reflect the guardrails in force (no AI gateway, no Semantic Kernel, no API keys for service auth).
    - Acceptance criteria: a reader following any documented runbook end to end hits no stale command, variable, or code reference, and every doc states its current status.

14. P2 — Use the `customer-profile` prompt agent in the product — **Delivered**
    - **Delivered.** The acceptance criteria below were observed against a live deployment: a preference stated in one Web UI session changed the response in a later, separate session for the same user and only that user. What follows records why the shape is what it is.
    - A `profile` step now runs ahead of the planner in [`AgentFrameworkWorkflowOrchestrator`](../src/application/AgentFrameworkWorkflowOrchestrator.cs) (`ExecuteProfileStepAsync`), and the preferences it recalls are passed to the planner and into the specialist's `context` dictionary. The step is fail-open: a workflow still completes when the profile agent is undeployed, unreachable, or scoped to somebody else, because losing personalisation is a better outcome than refusing a banking request.
    - The identity problem is addressed rather than solved. `WorkflowState.CustomerId` carries the signed-in user's object identifier from the Web UI into the background worker, because workflows execute long after the submitting request has ended and a user token would not survive the gap. Persisting user tokens to bridge that gap was rejected: it would put user credentials at rest in the workflow store.
    - Because the orchestrator still calls Foundry as its own managed identity, the scope is *asserted* by the application rather than derived from a user token. `CustomerProfileClient.EnforceScope` discards any memory returned under a different scope than the one requested, so a scope the service accepts and silently ignores costs personalisation instead of leaking one customer's details into another customer's workflow.
    - **Answered.** [`verify-memory-scope.py`](../scripts/verify-memory-scope.py) was run against a live project. Foundry ignores a scope passed next to an `agent_reference` — it accepts the field, returns `200`, and both scopes read the same fact — but honours the scope when the agent definition is sent inline, where a second scope reads nothing. `CustomerProfileClient` therefore sends scoped turns inline. The returned memories also carry their real stored scope, `<objectId>_<tenantId>` of the caller, which is why `EnforceScope` was effective rather than merely defensive.
    - The write path was the other half, and it was missing: `/Profile` sent turns unscoped while the workflow read a customer scope, so nothing ever wrote where the workflow looked. Both now carry the signed-in customer. This class of bug is silent — the write succeeds and simply lands where nothing reads — so it is pinned by `CustomerProfileEndpointScopeTests`.
    - Acceptance criteria: a preference stated in one Web UI session changes the response in a later, separate session for the same user and only that user, and the call is visible in Application Insights.

15. P2 — ~~Define a banking Responsible AI policy as code~~ — **closed, not planned**
    - Closed as not planned. The agent relies on the model deployment's own default content filter, which is acceptable for a demonstration environment. Reopen if this repository is ever taken toward production use. The findings below are kept because they are non-obvious and would otherwise have to be rediscovered.
    - The deployer supports attaching a content safety guardrail to the `customer-profile` prompt agent, but no policy is configured, so `rai_config` is omitted and the agent relies on the model deployment's own default content filter.
    - Sending an empty `rai_config` to select the platform default does **not** work, despite the published guidance saying it does. Api-version `v1` rejects it with `invalid_payload`: "Required property 'rai_policy_name' is missing", and the failure takes down the whole agent deploy. `test_memory_agent_omits_rai_config_when_no_policy_is_configured` pins this. A policy ARM resource ID is therefore mandatory, not optional, which makes this item a prerequisite for any agent-boundary guardrail rather than a refinement.
    - Create a `Microsoft.CognitiveServices/accounts/raiPolicies` resource on the Foundry account in [`ai.tf`](../infrastructure/ai.tf) with thresholds set deliberately for a banking assistant, expose its ARM resource ID as a Terraform output, and pass it to the deployer as `MEMORY_AGENT_RAI_POLICY`. The deployer already reads that variable and sends it as `rai_config.rai_policy_name`, so this is Terraform and wiring only.
    - Note that every api-version for this resource type is preview (`2025-10-01-preview` is the current default), which is why it was kept off the critical path rather than added during an end-to-end deployment.
    - Consider whether the same policy should be attached to the four hosted agents, which accept the identical `rai_config` field on their own definitions.
    - Acceptance criteria: `GET /agents/customer-profile/versions/{n}` returns the bank's own policy ARM ID in `definition.rai_config.rai_policy_name`, and a prompt the policy is configured to block returns HTTP 400 with a `content_filter` error rather than a model response.

16. P2 — Propagate the signed-in user with a delegated user token — **closed, not planned**
    - Closed as not planned. What shipped is enough: `enable_user_delegation` works, and with the flag off the orchestrator still asserts the customer scope on the Web UI's word, which is sound while the orchestrator has internal ingress and the Web UI is its only caller. The remaining work — replacing the confidential-client secret with a federated identity credential, and moving the token cache and Data Protection key ring out of process — is production hardening this prototype is not pursuing. The record below is kept because the dead ends in it are expensive to rediscover.
    - **Delivered behind `enable_user_delegation`, off by default.** The Web UI signs the user in itself with OpenID Connect and acquires an orchestrator token for them; see [ADR 0005](decisions/0005-delegated-user-authentication.md) for why, and the operations guide for setup. What follows records why the shape is what it is.
    - Before this, the Web UI read the platform-verified `X-MS-CLIENT-PRINCIPAL-ID` header and passed that object ID to the orchestrator as a *value*, which the orchestrator asserted as the memory scope while calling Foundry as its own managed identity. It was identity propagation by assertion, not by token. The orchestrator trusted the caller to state who the customer is, which is only sound because the orchestrator has internal ingress and the Web UI is the sole caller. That remains the behaviour with the flag off.
    - **This was never needed for per-customer memory.** Inline scoping already isolates memory per customer, verified by [`verify-memory-scope.py`](../scripts/verify-memory-scope.py) and by the absence of the `EnforceScope` mismatch warning in a live run. The case for this item is the trust model: a user token lets the orchestrator verify the customer rather than take the Web UI's word for it, and makes the identity chain demonstrable rather than asserted.
    - Feasible half: a delegated token between the Web UI and the orchestrator. Both applications can be registered in a tenant the operator controls, so the `api://` identifier URI that issue #30 is blocked on is available there. Note this does **not** also unblock `enable_service_auth`, which acquires its token from a managed identity — a managed identity can only obtain tokens from its own home tenant, so it cannot get one for an application registered elsewhere. This sidesteps that only because it uses the application's own client credential rather than the managed identity.
    - **On-behalf-of was built first and then abandoned, for an environmental reason worth knowing.** OBO needs an incoming user token, and behind Easy Auth the only source is the token store, whose sole supported backing is a blob **SAS URL**. Subscription policy here sets `allowSharedKeyAccess = false` and silently reverts attempts to change it, so the SAS returns `403 KeyBasedAuthenticationNotPermitted` on first use while sign-in continues to look healthy. In-app OpenID Connect replaced it, and is the better shape anyway: an application that runs its own sign-in holds its own refresh token, so it can request the orchestrator's scope directly instead of exchanging an assertion. ADR 0005 has the detail.
    - Infeasible half: carrying the user token onward to Foundry. Its data plane authorises on Azure RBAC, so a delegated token is evaluated against the *user* principal and requires that user to exist in the deployment's tenant with a Foundry role. Real banking customers are not principals in the bank's Foundry tenant, so this is the wrong model regardless of whether the tenant permits it.
    - Architectural limit worth recording: workflows resume in the background through the recovery worker, long after a user token has expired. Delegation covers the interactive path only. Persisting refresh tokens to bridge that gap was rejected — see the header of [`apps/webui-auth.tf`](../apps/webui-auth.tf) — because it puts user credentials at rest in the workflow store.
    - Remaining: the confidential-client secret is a key at rest. ADR 0005 accepts it and records the condition under which it should go — a federated identity credential becomes viable once the registration and the managed identity can live in the same tenant, which is issue #30. Also remaining: the token cache and data protection key ring are in-process, so a revision restart forces one re-sign-in.
    - Acceptance criteria: met with `enable_user_delegation = true`. The orchestrator derives the customer from a validated token rather than a request field (`CustomerAssertionGuard`), a request asserting a different customer than the token carries is rejected with 403 (`CustomerAssertionGuardTests`), and the background workflow path continues to work without any user token because the recovery worker never passes through the guard.

17. P2 — Rewrite `demo-agent-memory-and-tools.md` as documentation rather than a talk track — **Delivered**
    - [`demo-agent-memory-and-tools.md`](./demo-agent-memory-and-tools.md) is the only place the memory design is explained end to end, but it is written for a presenter standing in front of an audience. It opens with "a ten-minute demonstration", tells the reader where to "spend the time", scripts four acts, and organises whole sections around "the questions that come up every time" and "the tests worth showing". A reader who wants to understand how memory works has to strip the performance out of it first.
    - The substance is good and mostly unavailable elsewhere, so the rewrite must preserve rather than summarise it: the hosted-agent versus prompt-agent contrast and what it says about owning the loop; the honest three-way memory-type table (user and session memory yes, procedural no); that extraction is a *second* model pass, which roughly doubles token cost and forced the deployment capacity from 10 to 100; `update_delay = 0` against a default of 300 seconds; the 30-day TTL; and above all the scoping finding — Foundry accepts a scope next to an `agent_reference`, returns `200`, and ignores it, so isolation requires sending the definition inline.
    - Restructure around what a reader needs rather than the order a demo runs: what the memory store is, how it is created and why it is not a Terraform resource, how scope is bound and why the obvious approach fails silently, what is retained and what is forbidden, and what the failure modes are. Keep the four acts as a how-to-run appendix or split them into their own file, but stop making the demo the spine.
    - Reconcile with [ADR 0003](decisions/0003-foundry-memory-prompt-agent.md) while doing it. The ADR still claims "No custom memory client code", which was true when written and is not now: `CustomerProfileClient` hand-writes `BuildScopedRequest`, `EnforceScope`, `ClearMemoriesAsync`, and `Parse` against the preview REST API. Neither document records why Agent Framework's own memory abstractions were not used, which is the first question a reader arriving from the Agent Framework documentation will ask — the short answer being that Agent Framework's C# memory surface (`AgentSession`, `ChatHistoryProvider`, `AIContextProvider`) is client-side conversation history, not server-side semantic extraction, and exposes no hook for the per-request scope override this design depends on.
    - Acceptance criteria: someone who has never seen the demonstration can read the document and correctly answer what kind of memory this is, why the store is created by the deployer rather than by Terraform, and why scoped turns are sent inline — without encountering stage directions. No claim in the document contradicts ADR 0003 or the code.
    - Delivered: the document now opens on what the agent is and how it works, and is organised around the reader's questions — the hosted/prompt split, the three-way memory-type table, how Foundry runs the loop, the scoping constraint and its silent failure, retention and exclusion, clearing, the infrastructure, where each piece lives in code, identity, how memory reaches the workflow, guardrails, limits, configuration, and tests. The four acts survive intact as *Appendix A: running the demonstration*, with the troubleshooting table. ADR 0003 was reconciled in item 18's work: the "No custom memory client code" claim now carries a correction, and a "Rejected: Agent Framework's memory abstractions" section was added and is summarised under *Limits and open questions*. `README.md` and `docs/README.md` no longer describe the file as a talk track.

18. P2 — Replace the hand-written memory REST calls with `Azure.AI.Projects` memory-store operations — **Delivered**
    - `CustomerProfileClient` talks to the memory store over raw `HttpClient` against `api-version=2025-11-15-preview`, and hand-walks the response JSON in `Parse` to pull memories out of the `memory_search_call` item. `Azure.AI.Projects` **2.0.0-beta.2** exposes `AIProjectMemoryStoresOperations` with `CreateMemoryStoreAsync`, `GetMemoryStoreAsync`, `UpdateMemoryStoreAsync`, `DeleteMemoryStoreAsync`, `SearchMemoriesAsync`, `UpdateMemoriesAsync`, `WaitForMemoriesUpdateAsync`, and `DeleteScopeAsync`, plus typed models (`MemoryStore`, `MemoryItem`, `MemorySearchItem`, `MemoryStoreSearchResponse`).
    - **`DeleteScopeAsync` is the reason to do this.** `ClearMemoriesAsync` previously deleted the entire store and recreated it from its own definition, because per-item deletion is rejected by the preview API for the identifiers memory search returns. That means **Clear all memories** wipes every customer's scope, not the caller's — a caveat the demo document has to warn readers about. A per-scope delete removes both the hack and the warning.
    - The package is already present as a transitive dependency of `Microsoft.Agents.AI.AzureAI` 1.0.0-rc5, so this is a direct `PackageReference` rather than a new dependency in the graph.
    - **This does not remove the inline-scope code.** `MemorySearchPreviewTool` exists in `Azure.AI.Projects` 2.0.0-beta.1 and is **absent** from 2.0.0-beta.2, so there is no typed way to attach or rewrite the memory tool on an agent definition in the version Agent Framework depends on. `BuildScopedRequest` and `EnforceScope` stay hand-written. Confirm this against the SDK changelog before starting, in case the type returns under a new name.
    - Rejected alternative: Agent Framework's memory abstractions. Its C# surface (`AgentSession`, `InMemoryChatHistoryProvider`, `ChatHistoryMemoryProvider`, `AIContextProvider`) is client-side conversation history and context injection, not server-side semantic extraction, and it offers no per-request hook to rewrite the memory tool's `scope`. Adopting it would also introduce thread continuity, which would destroy the property that makes recall demonstrable — that no turn carries `previous_response_id`, so anything recalled came from the store rather than the prompt. Record this in ADR 0003 as part of item 17.
    - Note that these APIs remain preview and the SDK is beta, so the same re-evaluation ADR 0003 records for the memory feature itself applies here. The gain is typed models and a correct clear operation, not stability.
    - Acceptance criteria: `ClearMemoriesAsync` removes only the requesting customer's scope and leaves another scope's memories intact, pinned by a test; no `HttpClient` call in `CustomerProfileClient` targets `/memory_stores`; and `CustomerProfileClientParsingTests` continues to pass against the typed models, still asserting that memories are read from the tool call and not from the model's prose.
    - **Delivered.** `ClearMemoriesAsync(string memoryScope, CancellationToken)` now calls `AIProjectMemoryStoresOperations.DeleteScopeAsync`; the store-recreate path and `SendMemoryStoreAsync` are gone, so no `HttpClient` call targets `/memory_stores`. The scope is required rather than optional — there is no "clear whatever the caller shares" option, because in a multi-customer store that is a request to delete other people's memories — and the `DELETE /api/v1/profile/memories` endpoint now takes a `customerId`, runs it through `ICustomerAssertionGuard` like the read and write paths, and returns 400 without one. That guard was missing before and the store-wide delete had masked it. Pinned by `CustomerProfileClearScopeTests` and two new cases in `CustomerProfileEndpointScopeTests`. The experimental `AAIP001` diagnostic is suppressed in `infrastructure.csproj` and the infrastructure test project, and the SDK type is kept out of `CustomerProfileClient`'s public constructor behind an internal `UseMemoryStores` seam.
