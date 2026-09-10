# Agent memory and Foundry-managed tools

How the `customer-profile` agent works: what its memory is, where it is
configured, how it is isolated per customer, and what it deliberately does not
do.

This is a reference document. If you are looking for the demonstration — the
four acts, what to say, what to do when it breaks — that is
[Appendix A](#appendix-a-running-the-demonstration).

## At a glance

| | |
| --- | --- |
| **What it is** | A Foundry `kind: prompt` agent with the managed `memory_search_preview` tool and a code interpreter |
| **Who runs the loop** | Foundry. There is no orchestration code, no retrieval code, and no prompt assembly on our side |
| **Where it is defined** | `memory_agent_instructions` and `memory_agent_tools` in [`apps/main.tf`](../apps/main.tf), applied by [`deploy.py`](../src/agents/deployer/deploy.py) |
| **Memory types enabled** | User memory and session memory. Not procedural memory |
| **Isolation** | Per customer, by sending the agent definition inline with the tool's `scope` rewritten |
| **Retention** | Model-driven extraction, governed by an explicit exclusion rule; 30-day TTL |
| **Authentication** | Microsoft Entra ID only. The Foundry account sets `disableLocalAuth = true` |
| **Status** | Preview APIs, behind the `enable_agent_memory` Terraform flag |

## Why there are two kinds of agent here

Four of the five agents in this repository — `workflow-planning`,
`transaction-explanation`, `suspicious-activity`, `dispute-planning` — are
**hosted agents**. They are containers running our own LangGraph code, and the
C# orchestrator calls them over MCP. We wrote the graphs, we build the image, we
deploy it.

`customer-profile` is a **prompt agent**. There is no image and no graph. We
hand Foundry a model, an instruction, and a list of tools, and Foundry runs the
loop: it decides when to search memory, when to write memory, and when to
execute Python.

| | Hosted agent | Prompt agent |
| --- | --- | --- |
| Who runs the loop | Our container | Foundry |
| Where the logic lives | `src/agents/python/app/` | A Terraform local |
| Tools | Called from our code via the toolbox | Declared inline, run by Foundry |
| Deploy | Build and push an image | Change a definition |

The split is not arbitrary, and [ADR 0003](decisions/0003-foundry-memory-prompt-agent.md)
records why. Foundry's memory tool attaches declaratively to **prompt** agents,
and memory is extracted from *conversation turns*. The hosted agents have no
conversations: the orchestrator invokes each one exactly once per workflow step
with a self-contained request, and the graph returns. There is no thread for
Foundry to observe, so the declarative path does not map onto them. Adding
memory to a hosted agent would mean calling the low-level memory APIs from
inside the container.

Keeping memory out of those four is also deliberate on its own merits. Their
routing, approval gating, and evidence rules stay deterministic and memory-free,
so nothing that decides whether a request needs human approval can be influenced
by remembered content.

## What kind of memory this is

Foundry's built-in memory is usually described in three parts. This store
enables two of them, and it is worth being specific about which, because "the
agent has memory" invites the assumption that it does the third.

| Type | What it means | Enabled here? |
| --- | --- | --- |
| **User memory** | Durable preferences and facts about a person, carried across sessions | **Yes** — `user_profile_enabled` |
| **Session memory** | Context held within one conversation thread | **Yes** — `chat_summary_enabled`, as rolling summaries |
| **Procedural memory** | The agent learning reusable playbooks across runs, so it develops competence rather than re-reading instructions | **No** |

User memory is what makes a preference stated in one conversation available in a
different one; those are the `user_profile` entries. The `chat_summary` entries
alongside them are session memory — an account of what happened, not a claim
about the person.

Procedural memory is the genuinely interesting one, and this store does not do
it. This agent remembers *the customer*, not *how to do its job better*. Its
instructions are fixed in Terraform and identical on every run.

## How Foundry runs it

**Reading.** When the model decides it needs to know something about the
customer, it calls `memory_search_preview`. That is a tool call, visible in the
response. It is semantic, not a key lookup: the store embeds memories with
`text-embedding-3-small` and matches on meaning, which is why "is there anything
I need for readability?" retrieves a memory phrased as "needs large-print
statements" without either sentence sharing a keyword.

**Writing.** After the turn, Foundry runs a second model pass over the exchange
to decide what — if anything — is worth keeping, and reconciles it with what is
already stored rather than appending. That pass uses `gpt-5.4-mini`, the same
deployment serving the conversation. Two consequences follow:

- **Token cost is roughly double what the visible conversation suggests.** This
  is why the model deployment's capacity was raised from 10 to 100; at 10 the
  rate limit was reached during a single clean run.
- **Extraction is asynchronous.** The reply returns before extraction finishes,
  so the store lags the answer by a moment. `update_delay` controls how long
  Foundry batches before extracting, and its default of 300 seconds means a
  stated preference is not recallable for five minutes. It is set to `0` here.

**Forgetting.** Entries carry a 30-day TTL (`default_ttl_seconds`), so the store
expires stale preferences without a cleanup job. Memories can also be deleted a
scope at a time — see [Clearing memories](#clearing-memories).

## Scoping: the constraint that shapes the design

This is the part worth reading closely, because the obvious approach does not
work and fails **silently** when it does not.

The deployed tool declares `"scope": "{{$userId}}"`, which Foundry substitutes
from *the caller's* Entra token. The caller is the orchestrator's managed
identity — not the customer — so on its own that template puts every customer in
one shared scope. Foundry reports the scope it actually stored against, and it
comes back as `<objectId>_<tenantId>` of whoever held the token.

Passing a scope next to an agent reference does not fix it. **Foundry accepts
the field, returns `200`, and ignores it.** Nothing errors; the memory is simply
written somewhere else.

Because that failure is invisible, isolation is demonstrated rather than
assumed. [`verify-memory-scope.py`](../scripts/verify-memory-scope.py) writes a
fact under one scope and *requires* that a second scope cannot read it. Run
against a live project, `agent_reference` fails that test and `inline` passes.

So per-customer memory comes from the application sending the agent's own
definition inline with the scope replaced, which
`CustomerProfileClient.BuildScopedRequest` does. The definition is read back
from Foundry rather than restated in C#, so Terraform stays the source of truth
for the model, instructions, and tools.

Two guards sit around that:

- **A request that cannot be scoped is not sent.** If the definition contains no
  memory tool to bind, `BuildScopedRequest` throws rather than falling back to
  the shared scope.
- **The reply is checked, not trusted.** `CustomerProfileClient.EnforceScope`
  discards any memory returned under a scope other than the one requested, and
  logs a warning. If the service ever ignores an inline scope too, the cost is
  lost personalisation rather than one customer's details surfacing in another's
  workflow.

This failure mode was designed for rather than discovered. The same class of bug
bit the write path once: `/Profile` sent turns unscoped while the workflow read
a customer scope, so nothing ever wrote where the workflow looked. Both now
carry the signed-in customer, pinned by `CustomerProfileEndpointScopeTests`.

## What is retained, and what is forbidden

What gets kept is decided by a model, not by a schema. In a banking assistant
the conversation is saturated with data that must not be retained, so the
exclusion rule is written out explicitly in `user_profile_details` in
[`apps/main.tf`](../apps/main.tf) rather than left to the model's judgement. The
deployer **fails to start** when it is missing or blank.

The instruction forbids retaining account numbers, card numbers, balances,
transaction amounts, government identifiers, credentials, precise location, date
of birth, and age.

Two things worth knowing about the edges:

- **Accessibility needs are deliberately permitted.** "Low vision" is health
  data, and it is retained on purpose: an assistant that forgets an
  accessibility need is worse than useless. A conscious inclusion, not an
  oversight.
- **Session summaries record refusals.** A `chat_summary` may note that
  sensitive data was *offered and refused*, without the values. The event stays
  auditable; the data is not retained.

These are mitigations, not guarantees. Extraction is probabilistic, so **memory
must not be treated as an audit record**, and the memory store is not the system
of record for anything.

## Clearing memories

Memories are cleared **one scope at a time**, through
`CustomerProfileClient.ClearMemoriesAsync`, which calls
`AIProjectMemoryStoresOperations.DeleteScopeAsync` from `Azure.AI.Projects`. On
the wire that is `POST /memory_stores/{name}:delete_scope?api-version=v1` with a
`{"scope": ...}` body.

The scope is required, not optional. There is no "clear whatever the caller
shares" mode, because in a multi-customer store that is a request to delete
other people's memories. `DELETE /api/v1/profile/memories` therefore takes a
`customerId`, validates it through `ICustomerAssertionGuard` exactly like the
read and write paths, and returns 400 without one.

> **This used to be much blunter.** Per-item deletion is rejected by the preview
> API for the identifiers memory search returns, so the original implementation
> deleted the entire store and recreated it from its own definition — meaning
> one customer pressing "clear" wiped every customer. `DeleteScopeAsync`
> replaced that. `scripts/demo-customer-profile.py --reset` was changed the same
> way and now clears only the caller's own scope.

## What the infrastructure is

Fewer moving parts than people expect. There is no vector database to run, no
cache, no state store, and no schema. **Storage is Microsoft-managed**: you do
not provision, size, or see the backing store. What you control is the
definition — which memory types are on, what may be retained, and for how long.

| Piece | Where | Note |
| --- | --- | --- |
| Foundry account and project | [`infrastructure/ai.tf`](../infrastructure/ai.tf) | `disableLocalAuth = true`, so key-based access is refused, not merely unused |
| Chat model deployment | `infrastructure/ai.tf` | `gpt-5.4-mini`. Serves the conversation **and** memory extraction |
| Embedding model deployment | `infrastructure/ai.tf` | `text-embedding-3-small`, for semantic retrieval. Deployed after the chat model because Cognitive Services rejects concurrent deployment writes to one account |
| Memory store | Created by [`deploy.py`](../src/agents/deployer/deploy.py) | `kind: default`. Not a Terraform resource — its control plane is the Foundry data plane API |
| Agent definition | [`apps/main.tf`](../apps/main.tf) → `deploy.py` | Instructions and the tool list, applied as a new agent version |
| Orchestrator identity and roles | [`apps/roles.tf`](../apps/roles.tf) | `Foundry Agent Consumer` to invoke; `Cognitive Services User` for the memory-store calls behind **Clear memories** |

The memory store is a data-plane object rather than an ARM resource, which is
why it is created by the deployer container instead of by Terraform. That is the
one seam in an otherwise Terraform-managed stack.

## Where this lives in the code

Symbols are named rather than line numbers: line anchors in this repository have
drifted to unrelated code within a few releases.

### How the memory store is created

- **`MemoryAgentDefinition.store_body`** in [`deploy.py`](../src/agents/deployer/deploy.py)
  builds the store: `kind: default`, the chat and embedding deployments, and the
  `options` block that turns on `user_profile_enabled` and
  `chat_summary_enabled` and sets `default_ttl_seconds`.
- **`FoundryClient.ensure_memory_store`** does a `GET` first and only `POST`s on
  a 404, so re-running the deployer never destroys accumulated memories.
  **Redeploying does not reset the store.**
- **`_memory_agent`** builds the definition from environment variables and
  returns `None` when `MEMORY_STORE_NAME` is empty, so an environment without an
  embedding deployment still deploys the four hosted agents. It also refuses to
  start when `MEMORY_USER_PROFILE_DETAILS` is missing.

### How the agent gets the memory tool

- **`MemoryAgentDefinition.tool`** returns the `memory_search_preview` entry:
  the store name, `scope: "{{$userId}}"`, and `update_delay`.
- **`MemoryAgentDefinition.definition`** puts that tool first and appends the
  rest, producing the `kind: prompt` definition Foundry stores.
- **`_memory_agent_tools`** reads `MEMORY_AGENT_TOOLS` and **rejects any `mcp`
  entry**. See [the MCP trap](#the-mcp-trap).
- **`FoundryClient.deploy_prompt_agent`** and **`_find_matching_prompt_version`**
  compare kind, model, instructions, and tools against existing versions and
  skip the write when they match, so an unchanged apply does not pile up
  versions.

Values come from Terraform: `memory_agent_tools`, `memory_user_profile_details`,
and `memory_agent_instructions` in [`apps/main.tf`](../apps/main.tf), passed as
environment variables in [`apps/agent-deployer.tf`](../apps/agent-deployer.tf).

### How the application calls it

- **`ICustomerProfileClient`** in [`ICustomerProfileClient.cs`](../src/application/ICustomerProfileClient.cs)
  is the application-layer contract, with `ProfileReply` and `ProfileMemory`.
  The interface lives in Application and the implementation in Infrastructure,
  so nothing above the boundary knows about Foundry.
- **`CustomerProfileClient.AskAsync`** posts to `/openai/v1/responses`.
  **There is no `previous_response_id`** — every turn is independent, so
  anything the agent recalls provably came from the store rather than the
  prompt.
- **`CustomerProfileClient.BuildScopedRequest`** and **`EnforceScope`** are the
  scoping guards described above.
- **`CustomerProfileClient.Parse`** reads the reply text, the tool types, and
  the memories **from the `memory_search_call` item** rather than from the
  model's prose, so what the UI lists is the store's account of itself.
- **`CustomerProfileClient.ClearMemoriesAsync`** deletes a single scope through
  the `Azure.AI.Projects` SDK.
- **`CustomerProfileEndpoints.MapCustomerProfileEndpoints`** in
  [`CustomerProfileEndpoints.cs`](../src/api/CustomerProfileEndpoints.cs)
  exposes the three `/api/v1/profile/...` routes and returns 503 when the agent
  is not configured.
- **`ProfileModel`** in [`Profile.cshtml.cs`](../src/webui/Pages/Profile.cshtml.cs)
  forwards to those routes.

Notice what is *not* in that list: no retrieval code, no embedding call, no
prompt assembly, no memory write. The client sends a message and reads a result.

### Where the authentication is

- **`CustomerProfileClient.AuthorizeAsync`** acquires a token for
  `https://ai.azure.com/.default` from `DefaultAzureCredential` and sends it as
  a bearer header. No key, and no connection string.
- The identity is the orchestrator's user-assigned managed identity, configured
  by `AZURE_CLIENT_ID` in [`apps/orchestrator.tf`](../apps/orchestrator.tf).
- Its role assignments are in [`apps/roles.tf`](../apps/roles.tf):
  `orchestrator_agent_consumer` (**Foundry Agent Consumer**, granting only
  `endpoints/interact/action`) and `cognitive_services_user`
  (**Cognitive Services User**, covering the memory-store calls).

## How the customer is identified

The token above decides the *fallback* scope — the one shared by every caller
when no customer is known. Per-customer scope is decided elsewhere, and which
chain is running is a deployment flag:

- **Default.** Container Apps built-in authentication establishes the person
  ([`apps/webui-auth.tf`](../apps/webui-auth.tf)), `EasyAuthCustomerAccessor`
  reads their object ID from the platform's headers, the Web UI passes it to the
  orchestrator as a value, and `CustomerProfileClient` binds the memory tool to
  it. The orchestrator trusts the caller to state who the customer is, which is
  sound only because the orchestrator has internal ingress and the Web UI is its
  sole caller.
- **With `enable_user_delegation`.** The Web UI signs the user in itself and
  sends the orchestrator a token for them, so the orchestrator *derives* the
  object ID instead of being told it. `CustomerAssertionGuard` compares the
  customer a request claims to act for against the `oid` claim in that token and
  returns 403 on a mismatch. See
  [ADR 0005](decisions/0005-delegated-user-authentication.md).

Either way, signing in as someone else makes the store answer differently.

Two limits are worth stating plainly:

- **Delegation covers the interactive path only.** Workflows resume through the
  recovery worker long after any user token has expired, so that path asserts
  the customer recorded on the workflow. Persisting user tokens to bridge the
  gap was rejected: it would put user credentials at rest in the workflow store.
- **It stops at the orchestrator.** Foundry's data plane authorises on Azure
  RBAC, so a delegated token would be evaluated against the *user* principal and
  would require every bank customer to hold a Foundry role in the bank's tenant.
  Foundry calls therefore use the orchestrator's managed identity with the
  customer's object ID asserted as a memory scope.

## How memory reaches the banking workflow

A `profile` step runs ahead of the planner in
`AgentFrameworkWorkflowOrchestrator` (`ExecuteProfileStepAsync`), and what it
recalls is passed to the planner and into the specialist agent's `context`
dictionary, so a stated contact or accessibility preference can shape both the
plan and the wording of the answer.

- **It fails open.** If the profile agent is undeployed, unreachable, slow, or
  scoped to somebody else, the workflow proceeds without personalisation. A
  customer disputing a transaction gets an answer whether or not the memory
  service is healthy. `ProfileStepFailOpenTests` pins this by running the whole
  workflow with a profile client that throws.
- **It cannot influence approval.** `WorkflowRoutingPolicy` can only escalate
  `requires_approval`, never remove it, so even a poisoned memory cannot talk
  the workflow past a human approval gate.

## Tool calling

The hosted LangGraph agents are reached as MCP tools over ordinary JSON-RPC 2.0
— no proprietary envelope. `FoundryMcpClient.BuildToolsCallRequest` builds a
`tools/call`; discovery is the same shape with `tools/list`. The request `id` is
a hash of the method and arguments rather than a counter, so a retried call
reuses its id and is idempotent to a server that deduplicates.

`McpFailureDescription` turns a transport or protocol failure into something a
person can act on, rather than an empty answer that looks like the agent simply
had nothing to say.

### The MCP trap

A prompt agent must **not** reach its tools through the project toolbox. Foundry
cannot bind a prompt agent's `mcp` tool to the agent identity — the tool's
`authorization` field is a literal header string — so the call is rejected with
a 401 at invocation time, and it takes the whole response down *including the
memory tool*. Deployment still reports success, so nothing catches it until
someone talks to the agent.

The toolbox is for the hosted container agents, which authenticate to it with
their own identity from inside the container. Prompt agents declare tools
inline. `_memory_agent_tools` rejects an `mcp` entry in `MEMORY_AGENT_TOOLS` for
this reason, with the reason in the error text.

## Guardrails

Azure's default content filter runs on the model deployment, screening prompts
and completions for hate, violence, sexual content, and self-harm, plus
prompt-injection detection on input. That applies to every agent here, because
they all call Foundry models.

Foundry also supports a guardrail attached to the *agent* itself via
`rai_config` on the agent definition, but it requires the ARM resource ID of a
Responsible AI policy the bank has defined. No such policy exists in this
deployment, so the field is deliberately omitted and the model-deployment filter
is the whole story today. Sending an empty `rai_config` to select a platform
default does **not** work: despite the published guidance, api-version `v1`
rejects `rai_config: {}` with "Required property 'rai_policy_name' is missing",
so `deploy.py` omits the field entirely when no policy is named. Backlog item 15
covered defining a policy as code and is closed as not planned.

Note that `WorkflowRoutingPolicy` is an approval control, not a safety control.
It answers a different question — it can only escalate `requires_approval`,
never remove it.

## Limits and open questions

- **Everything here is preview.** Memory in Foundry Agent Service and the Memory
  Store API are preview, served under api-version `2025-11-15-preview` (the
  agents API remains `v1`). The `Azure.AI.Projects` memory types are marked
  experimental (`AAIP001`), suppressed deliberately in
  `src/infrastructure/infrastructure.csproj`. Re-evaluate before any production
  use.
- **Not procedural memory.** The agent remembers the customer, not how to do its
  job better.
- **Not an audit record.** Extraction is probabilistic.
- **The scope is asserted, not derived, by default.** Only with
  `enable_user_delegation` does the orchestrator verify the customer from a
  token rather than take the Web UI's word for it.
- **Agent Framework's memory abstractions are not used**, and that is a decision
  rather than an oversight. Its C# surface — `AgentSession`,
  `InMemoryChatHistoryProvider`, `ChatHistoryMemoryProvider`,
  `AIContextProvider` — is client-side conversation history and context
  injection, not server-side semantic extraction. It also offers no per-request
  hook to rewrite the memory tool's `scope`, and `AgentSession` would introduce
  the thread continuity that currently makes recall demonstrable. See
  [ADR 0003](decisions/0003-foundry-memory-prompt-agent.md).

## Configuration reference

| Setting | Where | Why |
| --- | --- | --- |
| `enable_agent_memory` | `apps/variables.tf` | Master switch. When false, `local.memory_store_name` is empty, the deployer skips the store and the prompt agent, and the four hosted agents deploy as before |
| `memory_update_delay_seconds = 0` | `apps/variables.tf` | Foundry defaults to batching extraction. At 300s a preference is not recallable for five minutes |
| `capacity = 100` | `infrastructure/ai.tf` | Rate limit, not a reservation; GlobalStandard bills per token. Raised because memory extraction doubles token spend |
| `memory_agent_tools = [code_interpreter]` | `apps/main.tf` | Declared inline, not via the toolbox |
| `MEMORY_USER_PROFILE_DETAILS` | `apps/main.tf` | The retention and exclusion rule. The whole PII story is this string |
| `MEMORY_AGENT_NAME`, `MEMORY_STORE_NAME` | `apps/orchestrator.tf` | What the profile page needs to reach the agent. Empty when memory is disabled, which makes the page report "not configured" rather than fail obscurely |
| `user_profile_enabled`, `chat_summary_enabled` | [`deploy.py`](../src/agents/deployer/deploy.py) | Which memory types the store keeps. Both on; there is no procedural memory option |
| `default_ttl_seconds` | `deploy.py` | 30 days |
| `MEMORY_AGENT_RAI_POLICY` | Read by [`deploy.py`](../src/agents/deployer/deploy.py); not set by Terraform | Unset. See [Guardrails](#guardrails) |

## The tests that hold this in place

- **`CustomerProfileInlineScopeTests`** — the request Foundry receives is bound
  to the requested scope, and the code interpreter survives being sent inline.
- **`CustomerProfileEndpointScopeTests`** — the signed-in customer is carried
  from the page to the agent, and clearing without a customer is refused.
- **`CustomerProfileClearScopeTests`** — clearing deletes one scope and never
  names another.
- **`CustomerProfileClientParsingTests`** — memories are read from the tool call
  and not from the message; `message` is not reported as a tool.
- **`MemoryAgentConfigTests`** — a missing or blank retention rule fails the
  deploy, and the scope defaults to `{{$userId}}`.
- **`MemoryAgentToolTests`** — the `mcp` rejection.
- **`MemoryStoreProvisioningTests`** — create-once behaviour.
- **`ProfileStepFailOpenTests`** — a workflow completes when the profile agent
  throws.

## Where the traces are

- **Foundry portal** → the project → the agent → its runs.
- **Application Insights**: the workspace is shared with the rest of the app.
  The orchestrator's OpenTelemetry spans cover the `profile` workflow step; the
  profile *page* talks to the agent directly.

---

## Appendix A: running the demonstration

A ten-minute walkthrough of memory and a Foundry-managed tool working together
on Entra ID alone. Run it from the Web UI — **Customer profile agent** in the
header — or from a terminal with `scripts/demo-customer-profile.py`, which does
the same four acts and shows the raw JSON.

> **The page and the script do not share a memory scope.** Foundry derives the
> scope from the caller's token, so the page writes as the orchestrator's
> managed identity and the script writes as you. Memories set in one are
> invisible to the other. Pick one and stay in it.

### Before you start

Press **Clear memories**, or run `task app:demo-customer-profile -- --reset`.

Reset matters more than it looks. The demonstration's claim is that act 2
recalls something act 1 stored. If a previous rehearsal left memories behind,
act 2 will look like it works **even if act 1 failed**, and you will not find
out on stage. Note that clearing is per scope, so clearing from the page does
not clear the script's memories, or the other way round.

### The four acts

Each act is a separate HTTP request with **no `previous_response_id`**. There is
no conversation history. That is the control: anything recalled came out of the
memory store, not out of the prompt.

**Act 1 — the customer states a preference.** Chips:
`memory_command_preview_call`, `memory_search_call`. Nothing extracted that
preference by hand and there is no schema for it; a second model pass looked at
the exchange and decided there was something durable in it. The only thing
configured is what is *allowed* to be kept.

**Act 2 — a new conversation. Does it remember?** Chip: `memory_search_call`.
New request, no history, so it has to go and look — and it never asks who the
user is. The scope line under the memories shows the signed-in customer's object
ID, which came from the platform's authentication rather than from anything
typed into the page. The retrieval is semantic: the question says "readability"
and the memory says "large-print statements", sharing no keyword.

**Act 3 — the customer volunteers PII.** The agent keeps the marketing
preference and refuses the card number, balance, and date of birth. What gets
kept is decided by a model, so the exclusion rule has to be explicit
configuration rather than a hope — show `memory_user_profile_details` in
[`apps/main.tf`](../apps/main.tf).

**Act 4 — prove it, and use a tool.** Chips: `code_interpreter_call`,
`memory_search_call`. Foundry started a sandbox, executed Python, and read the
result back; the numbers are past what a model does reliably in its head. Then
the proof: the memories listed on the page are read from the
`memory_search_call` item — the store's own account of itself — and what is
absent is the point. No card number, no balance, no date of birth. Only
servicing preferences.

### If it goes wrong

| Symptom | Cause | What to do |
| --- | --- | --- |
| `rate_limited; retrying in Ns` | Memory extraction runs the chat model again after every turn, so a demo spends roughly double the visible tokens | The script waits it out. Keep talking; it recovers |
| Act 2 recalls nothing | Extraction had not finished | Re-run act 2: `-- --act 2`. Raise `--settle` |
| Act 2 recalls things you never said | A previous run was not cleared | `-- --reset`, then start again |
| `tool_user_error` about MCP auth | The agent has been pointed at the toolbox again | See [the MCP trap](#the-mcp-trap) |
| 404 on the agent | Agents not deployed | `task app:deploy-hosted-agents` |
| The page says the agent is not configured | `MEMORY_STORE_NAME` is empty, so `enable_agent_memory` is false | `task app:apply` with memory enabled |
| Clearing returns 400 | Nobody is signed in, so there is no scope to clear | Sign in, then clear |
| The UI shows no memories the script just stored | Different scopes — see the warning above | Present from one surface only |

### The closing point

Everything in the demonstration — creating the agent, calling it, reading
memory, executing the tool — ran on Microsoft Entra ID. No API keys, no
connection strings. The Foundry account sets `disableLocalAuth = true`, so
key-based access is refused, not merely unused.
