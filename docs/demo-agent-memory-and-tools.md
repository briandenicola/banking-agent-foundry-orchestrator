# Talk track: agent memory and tools in Microsoft Foundry

A ten-minute demonstration of the `customer-profile` agent, showing Foundry-managed
memory and a Foundry-managed tool working together on Entra ID alone.

Run it from the Web UI: **Customer profile agent** in the header. The page has a
card per act, a message box, and a live view of the memory store, so the mechanics
need no narration — spend the time on what is happening underneath, which is what
the rest of this document is for.

The `scripts/demo-customer-profile.py` driver does the same four acts from a
terminal. Use it for rehearsal and for seeing the raw JSON.

> **The page and the script do not share a memory scope.** Foundry derives the
> scope from the caller's token, so the page writes as the orchestrator's managed
> identity and the script writes as you. Memories set in one are invisible to the
> other. Pick one and stay in it. **Clear all memories** is the exception: it
> recreates the store, which wipes every scope.

## What this agent is, and why it is different

The four agents the rest of this repository talks about — `workflow-planning`,
`transaction-explanation`, `suspicious-activity`, `dispute-planning` — are **hosted
agents**. They are containers running our own LangGraph code, and the C#
orchestrator calls them over MCP. We wrote the graphs, we build the image, we
deploy it.

`customer-profile` is a **prompt agent**. There is no image and no graph. We hand
Foundry a model, an instruction, and a list of tools, and Foundry runs the loop:
it decides when to search memory, when to write memory, and when to execute
Python. The entire agent is the definition in
[`apps/main.tf`](../apps/main.tf) — `memory_agent_instructions` and
`memory_agent_tools`.

That contrast is the opening: **the same project runs both kinds, and the choice is
about how much of the loop you want to own.**

| | Hosted agent | Prompt agent |
| --- | --- | --- |
| Who runs the loop | Our container | Foundry |
| Where the logic lives | `src/agents/python/app/` | A Terraform local |
| Tools | Called from our code via the toolbox | Declared inline, run by Foundry |
| Deploy | Build and push an image | Change a definition |

> **Honesty note.** The banking workflow does not call `customer-profile`. The
> profile page talks to it directly, so it is reachable from the product but is not
> yet *part* of the product: no plan, approval, or specialist response is
> personalised by it. Backlog item 14 tracks that. If asked "is this in your app?",
> the honest answer is "it is deployed, and we can talk to it from our UI, but the
> workflow does not use it yet."

## What kind of memory this is

Foundry's built-in memory is usually described in three parts. This store enables
two of them, and it is worth saying which, because "the agent has memory" invites
an audience to assume the third.

| Type | What it means | Here? |
| --- | --- | --- |
| **User memory** | Durable preferences and facts about a person, carried across sessions | **Yes** — `user_profile_enabled` |
| **Session memory** | Context held within one conversation thread | **Yes** — `chat_summary_enabled`, as rolling summaries |
| **Procedural memory** | The agent learning reusable playbooks across runs, so it develops competence rather than re-reading instructions | **No** |

The demonstration turns on **user memory**: a preference stated in one conversation
is available in a different one. That is the `user_profile` kind in the memory list
on screen. The `chat_summary` entries alongside it are session memory — an account
of what happened, not a claim about the person.

Procedural memory is the genuinely interesting one, and this store does not do it.
If asked, the honest answer is that this agent remembers *the customer*, not *how
to do its job better*. Its instructions are fixed in Terraform and identical on
every run.

## How Foundry actually runs it

Nothing here is orchestrated by us. It is worth being concrete about that, because
"the platform handles it" is the claim the audience is most likely to discount.

**Reading.** When the model decides it needs to know something about the customer,
it calls `memory_search_preview`. That is a tool call, visible in the response and
on screen as a chip. It is semantic, not a key lookup: the store embeds memories
with `text-embedding-3-small` and matches on meaning, which is why "is there
anything I need for readability?" retrieves a memory phrased as "needs large-print
statements" without either sentence sharing a keyword.

**Writing.** After the turn, Foundry runs a second model pass over the exchange to
decide what — if anything — is worth keeping, and reconciles it with what is
already stored rather than appending. That pass uses `gpt-5.4-mini`, the same
deployment serving the conversation. Two consequences that matter:

- **A demonstration costs roughly double the tokens the visible conversation
  suggests.** This is why the model deployment's capacity was raised from 10 to
  100; at 10 the rate limit was reached during a single clean run.
- **It is asynchronous.** The reply returns before extraction finishes, so the
  store lags the answer by a moment. `update_delay` controls how long Foundry
  batches before extracting, and its default of 300 seconds means a stated
  preference is not recallable for five minutes. It is set to `0` here.

**Scoping.** This is worth getting right, because the obvious answer is wrong and
we have the probe to prove it.

The tool declares `"scope": "{{$userId}}"`, which Foundry substitutes from *the
caller's* Entra token. The caller here is the orchestrator's managed identity —
not the customer — so on its own that template puts every customer in one shared
scope. Foundry reports the scope it actually stored against, and it comes back as
`<objectId>_<tenantId>` of whoever held the token.

Passing a scope next to an agent reference does not fix it: Foundry accepts the
field, returns `200`, and ignores it. Isolation has to be demonstrated rather
than assumed, which is what
[`verify-memory-scope.py`](../scripts/verify-memory-scope.py) does — it writes a
fact under one scope and *requires* that a second scope cannot read it. Against
the live project, `agent_reference` fails that test and `inline` passes.

So per-customer memory comes from the application sending the agent's own
definition inline with the scope replaced, which
`CustomerProfileClient.BuildScopedRequest` does. The definition is read back from
Foundry rather than restated, so Terraform stays the source of truth for the
model, instructions, and tools.

`CustomerProfileClient.EnforceScope` then discards any memory returned under a
different scope than the one asked for. If the service ever ignores an inline
scope too, the cost is lost personalisation and a warning — not one customer's
details surfacing in another's workflow.

**Forgetting.** Entries carry a 30-day TTL (`default_ttl_seconds`), so the store
expires stale preferences without anyone running a cleanup job.

**What gets kept** is model-driven, which is exactly why the exclusion rule is
written out in `user_profile_details` rather than left to judgement. In a banking
assistant the conversation is saturated with the data you must not retain.

## What the infrastructure actually is

Fewer moving parts than people expect. There is no vector database to run, no
cache, no state store, and no schema.

| Piece | Where | Note |
| --- | --- | --- |
| Foundry account and project | [`infrastructure/ai.tf`](../infrastructure/ai.tf) | `disableLocalAuth = true`, so key-based access is refused, not merely unused |
| Chat model deployment | `infrastructure/ai.tf` | `gpt-5.4-mini`. Serves the conversation **and** memory extraction |
| Embedding model deployment | `infrastructure/ai.tf` | `text-embedding-3-small`, for semantic retrieval. Deployed after the chat model because Cognitive Services rejects concurrent deployment writes to one account |
| Memory store | Created by [`deploy.py`](../src/agents/deployer/deploy.py) | `kind: default`. Not a Terraform resource — the control plane for it is the Foundry data plane API |
| Agent definition | [`apps/main.tf`](../apps/main.tf) → `deploy.py` | Instructions and the tool list, applied as a new agent version |
| Orchestrator identity and roles | [`apps/roles.tf`](../apps/roles.tf) | `Foundry Agent Consumer` to invoke; `Cognitive Services User` for the memory-store calls behind **Clear all memories** |

The memory store is a data-plane object rather than an ARM resource, so it is
created by the deployer container alongside the agents instead of by Terraform.
That is the one seam in an otherwise Terraform-managed stack, and it is why
clearing memories from the page recreates the store rather than deleting rows.

**Storage is Microsoft-managed.** You do not provision or see the backing store,
and you do not size it. What you control is the definition: which memory types are
on, what may be retained, and for how long.

## Where memory lives in this repository

Follow the four questions an engineer in the audience will actually ask. Symbols
are named rather than line numbers, because line anchors in this repository have
drifted to unrelated code within a few releases.

### 1. How is the memory store created?

Not by Terraform. A Foundry memory store is a **data-plane** object, so the
agent-deployer container creates it over the REST API.

- **`MemoryAgentDefinition.store_body`** in [`deploy.py`](../src/agents/deployer/deploy.py)
  builds the store: `kind: default`, the chat and embedding deployments, and the
  `options` block that turns on `user_profile_enabled` and `chat_summary_enabled`
  and sets `default_ttl_seconds`.
- **`FoundryClient.ensure_memory_store`** does a `GET` first and only `POST`s on a
  404, so re-running the deployer never destroys accumulated memories. Worth
  knowing before a demonstration: **redeploying does not reset the store.** Only
  **Clear all memories** does.
- **`_memory_agent`** builds the definition from environment variables and returns
  `None` when `MEMORY_STORE_NAME` is empty, so an environment without an embedding
  deployment still deploys the four hosted agents.

That last function also refuses to start when `MEMORY_USER_PROFILE_DETAILS` is
missing. Extraction is model-driven, so shipping without a retention rule would
mean whatever the model considered interesting — in a banking transcript, exactly
the wrong thing.

### 2. How does the agent get the memory tool?

- **`MemoryAgentDefinition.tool`** returns the `memory_search_preview` entry: the
  store name, `scope: "{{$userId}}"`, and `update_delay`.
- **`MemoryAgentDefinition.definition`** puts that tool first and appends the rest,
  producing the `kind: prompt` definition Foundry stores.
- **`_memory_agent_tools`** reads `MEMORY_AGENT_TOOLS` and **rejects any `mcp`
  entry**, with the reason in the error text. That guard exists because attaching
  the toolbox here is what made the agent return `tool_user_error` on every call.
- **`FoundryClient.deploy_prompt_agent`** and **`_find_matching_prompt_version`**
  compare kind, model, instructions and tools against the existing versions and
  skip the write when they match, so an unchanged apply does not pile up versions.

The values come from Terraform: `memory_agent_tools`, `memory_user_profile_details`
and `memory_agent_instructions` in [`apps/main.tf`](../apps/main.tf), passed as
environment variables in [`apps/agent-deployer.tf`](../apps/agent-deployer.tf).

### 3. How does the application call it?

- **`ICustomerProfileClient`** in
  [`ICustomerProfileClient.cs`](../src/application/ICustomerProfileClient.cs) is the
  application-layer contract, with `ProfileReply` and `ProfileMemory`. The
  interface lives in Application and the implementation in Infrastructure, so
  nothing above the boundary knows about Foundry.
- **`CustomerProfileClient.AskAsync`** in
  [`CustomerProfileClient.cs`](../src/infrastructure/CustomerProfileClient.cs) posts
  to `/openai/v1/responses` with `agent_reference`. **There is no
  `previous_response_id`** — that omission is what makes act 2 evidence rather than
  theatre, so it is worth pointing at directly.
- **`CustomerProfileClient.Parse`** reads the reply text, the tool types, and the
  memories **from the `memory_search_call` item** rather than from the model's
  prose. This is the single most important method for the talk: it is why the list
  on screen is the store's account of itself.
- **`CustomerProfileClient.ClearMemoriesAsync`** deletes and recreates the store,
  because per-item deletion is rejected by the preview API for the identifiers
  memory search returns.
- **`CustomerProfileEndpoints.MapCustomerProfileEndpoints`** in
  [`CustomerProfileEndpoints.cs`](../src/api/CustomerProfileEndpoints.cs) exposes
  the three `/api/v1/profile/...` routes and returns 503 when the agent is not
  configured.
- **`ProfileModel`** in [`Profile.cshtml.cs`](../src/webui/Pages/Profile.cshtml.cs)
  forwards to those routes; its `Prompts` collection is the four acts.

Notice what is *not* in this list: no retrieval code, no embedding call, no
prompt assembly, no memory write. The client sends a message and reads a result.

### 4. Where is the authentication?

- **`CustomerProfileClient.AuthorizeAsync`** acquires a token for
  `https://ai.azure.com/.default` from `DefaultAzureCredential` and sends it as a
  bearer header. No key, and no connection string.
- The identity is the orchestrator's user-assigned managed identity, configured by
  `AZURE_CLIENT_ID` in [`apps/orchestrator.tf`](../apps/orchestrator.tf).
- Its two role assignments are in [`apps/roles.tf`](../apps/roles.tf):
  `orchestrator_agent_consumer` (**Foundry Agent Consumer**, which grants only
  `endpoints/interact/action` — enough to invoke) and `cognitive_services_user`
  (**Cognitive Services User**, which covers the memory-store calls behind
  **Clear all memories**).

That token is what Foundry resolves `{{$userId}}` from — so these two files decide
the *fallback* scope, the one shared by every caller when no customer is known.

Per-customer memory is decided elsewhere, and it is worth being able to point at
the chain. There are two, and which one is running is a deployment flag:

- **Default.** Easy Auth establishes the person
  ([`apps/webui-auth.tf`](../apps/webui-auth.tf)), `EasyAuthCustomerAccessor` reads
  their object ID out of the platform's headers, the Web UI passes it to the
  orchestrator as a value, and `CustomerProfileClient` binds the memory tool to
  it.
- **With `enable_user_delegation`.** The Web UI signs the user in itself and sends
  the orchestrator a token for them, so the orchestrator *derives* the object ID
  instead of being told it. The next section is the code.

Either way, sign in as someone else and the store answers differently.

### The tests worth showing

If someone asks how any of this is held in place:

- **`CustomerProfileClientParsingTests`** in
  [the Infrastructure tests](../tests/BankingAgent.Infrastructure.Tests/CustomerProfileClientParsingTests.cs)
  pins that memories are read from the tool call and not the message, and that
  `message` is not reported as a tool.
- **`MemoryAgentToolTests`** in
  [`test_deploy.py`](../src/agents/deployer/test_deploy.py) pins the `mcp`
  rejection.
- **`MemoryStoreProvisioningTests`** pins the create-once behaviour
  (`test_existing_store_is_not_recreated`).
- **`MemoryAgentConfigTests`** pins that a missing or blank retention rule fails
  the deploy, and that the scope defaults to `{{$userId}}`
  (`test_memory_agent_defaults_to_per_user_scope`).
- **`CustomerProfileInlineScopeTests`** and **`CustomerProfileEndpointScopeTests`**
  hold the per-customer claim itself: that the request Foundry receives is bound
  to the requested scope, that the code interpreter survives being sent inline,
  and that the signed-in customer is carried all the way from the page to the
  agent. The last one matters most — a turn written to the wrong scope fails
  silently, since the write succeeds and simply lands where nothing reads.

## The code, when someone asks to see it

Three questions come up every time: how do you know *who* the customer is, how
does memory get bound to them, and what actually happens when the agent uses a
tool. Each is a handful of lines, and showing them is more convincing than the
architecture diagram.

### Identity: the orchestrator derives the customer, it is not told

The interesting part is what is **absent**. With `enable_user_delegation` on, the
Web UI never sends a customer identifier the orchestrator trusts. It sends a
token, and the orchestrator reads the identity out of it.

The Web UI is a confidential client running its own OpenID Connect sign-in, so it
already holds a refresh token for this user and can ask Entra for a token
addressed to the orchestrator — `DelegatedUserTokenHandler`:

```csharp
// ITokenAcquisition is scoped, and message handlers are pooled across requests,
// so a constructor-injected copy would outlive the scope it came from.
var tokenAcquisition = context.RequestServices.GetRequiredService<ITokenAcquisition>();

var token = await tokenAcquisition.GetAccessTokenForUserAsync([scope], user: context.User);

request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
```

On the other side, `CustomerAssertionGuard` compares the customer the request
claims to act for against the `oid` claim in that token:

```csharp
var subject = ReadObjectId(httpContextAccessor.HttpContext?.User);

if (string.IsNullOrWhiteSpace(subject))
{
    return Results.Problem(
        title: "The request did not carry a user identity.",
        statusCode: StatusCodes.Status401Unauthorized);
}

if (!string.Equals(subject, assertedCustomerId, StringComparison.OrdinalIgnoreCase))
{
    return Results.Problem(
        title: "The request asked to act for a different customer than the token identifies.",
        statusCode: StatusCodes.Status403Forbidden);
}
```

**Call this what it is.** It is not on-behalf-of, and the difference is worth
being straight about if an architect in the room asks. OBO was built first and
abandoned: it needs an incoming user token, which behind Easy Auth means the
token store, whose only supported backing is a blob **SAS URL** — and the target
subscription's policy forbids shared-key storage access and silently reverts
attempts to enable it. The SAS returns `403 KeyBasedAuthenticationNotPermitted`
while sign-in continues to look perfectly healthy.

An application that runs its own sign-in never needed the exchange anyway: OBO
exists for a middle tier that receives a token it did not request, which is not
this. The security property is identical — a token issued to the Web UI, for this
user, with the orchestrator as its audience — and Entra can say so.
[ADR 0005](decisions/0005-delegated-user-authentication.md) has the full account.

The honest limits, which are better volunteered than extracted:

- It covers the **interactive path only**. Workflows resume through the recovery
  worker long after any user token has expired, so that path still asserts the
  customer recorded on the workflow.
- It **stops at the orchestrator**. Foundry's data plane authorises on Azure
  RBAC, so a delegated token would be evaluated against the *user* principal and
  would require every bank customer to hold a Foundry role in the bank's tenant.
  Foundry calls use the orchestrator's managed identity with the customer's object
  ID asserted as a memory scope.

### Memory: one tool entry, and the scope that makes it per-customer

The agent-side definition is four lines — `MemoryAgentDefinition.tool` in
[`deploy.py`](../src/agents/deployer/deploy.py):

```python
{
    "type": "memory_search_preview",
    "memory_store_name": self.memory_store_name,
    "scope": self.scope,          # "{{$userId}}" by default
    "update_delay": self.update_delay_seconds,
}
```

`{{$userId}}` is resolved by Foundry from the calling token, which means the
*orchestrator's* managed identity — one shared scope for every customer. That is
the fallback, not the goal. To bind a turn to a specific person the request is
sent **inline** instead of by agent reference, with the tool's scope rewritten,
in `CustomerProfileClient.BuildScopedRequest`:

```csharp
case MemoryToolType:
    tool["scope"] = scope;
    scoped++;
    break;
```

```csharp
if (scoped == 0)
{
    throw new CustomerProfileException(
        "The profile agent has no memory tool to scope; refusing to send an unscoped request.");
}
```

That refusal is the point: a request that cannot be scoped is not sent at all,
rather than quietly falling back to the shared scope.

Foundry reports the scope it actually used, so the reply is checked rather than
assumed — `EnforceScope`:

```csharp
var kept = reply.Memories
    .Where(memory => string.Equals(memory.Scope, requestedScope, StringComparison.Ordinal))
    .ToList();
```

If the service ever ignores the requested scope, this turns a data leak into lost
personalisation, and logs a warning saying so. Worth saying out loud: the failure
mode was designed for, not discovered.

### Tool calling: ordinary MCP, no bespoke protocol

The hosted LangGraph agents are reached as MCP tools over JSON-RPC 2.0. There is
no proprietary envelope — `FoundryMcpClient.BuildToolsCallRequest`:

```csharp
new()
{
    ["jsonrpc"] = "2.0",
    ["id"] = CreateRequestId("tools/call", toolName, parameters),
    ["method"] = "tools/call",
    ["params"] = new Dictionary<string, object?>
    {
        ["name"] = toolName,
        ["arguments"] = parameters
    }
};
```

Discovery is the same shape with `tools/list`. The request `id` is a hash of the
method and arguments rather than a counter, so a retried call reuses its id and
is idempotent to a server that deduplicates.

Two things are worth pointing at while this is on screen:

- **The memory agent must not be given MCP tools.** `_memory_agent_tools` rejects
  any `mcp` entry outright, because attaching the toolbox to the prompt agent made
  it return `tool_user_error` on every single call. The guard carries the reason
  in its error text.
- **Tool failures are surfaced, not swallowed.** `McpFailureDescription` turns a
  transport or protocol failure into something a person can act on, instead of an
  empty answer that looks like the agent simply had nothing to say.

## Before you start

Press **Clear all memories**, or `task app:demo-customer-profile -- --reset`.

Reset matters more than it looks. The demonstration's whole claim is that act 2
recalls something act 1 stored. If a previous rehearsal left memories behind, act 2
will look like it works **even if act 1 failed**, and you will not find out on
stage.

## The four acts, and what to say during each

Each act is a separate HTTP request with **no `previous_response_id`**. There is no
conversation history. That is the control, and it is worth stating once at the
start: anything recalled came out of the memory store, not out of the prompt.

The page tells the audience what each act is for, so do not read the cards out.
Narrate the part that is not on screen.

### Act 1 — the customer states a preference

Chips: `memory_command_preview_call`, `memory_search_call`.

> "I did not write code to extract that preference, and there is no schema for it.
> A second model pass looked at the exchange and decided there was something
> durable in it. The only thing I configured was what is *allowed* to be kept."

### Act 2 — a new conversation. Does it remember?

Chip: `memory_search_call`.

> "New request, no history — it has to go and look. And notice what it did not do:
> it never asked me who I was."

Then point at the scope line under the memories:

```
Scope 98abb71d-… — the signed-in customer's object ID, asserted by the orchestrator.
```

That object ID came from Easy Auth, not from anything typed into the page. It is
worth being precise about who decides it, because the intuitive answer is wrong:
Foundry resolves the agent's `{{$userId}}` template from *the caller's* token, and
the caller is the orchestrator's managed identity — one scope for everybody. The
orchestrator asserts the customer's scope explicitly instead. Sign in as somebody
else and this store is empty.

If you are asked how that is enforced rather than merely intended: memories
returned under any other scope are discarded before they reach this page, so a
scope the service ignored costs personalisation instead of showing one customer
another's details.

The retrieval is also semantic: the question says "readability" and the memory says
"large-print statements". No keyword is shared.

### Act 3 — the customer volunteers PII

The agent keeps the marketing preference and refuses the card number, balance and
date of birth.

> "What gets kept is decided by a model, so in a banking assistant the exclusion
> rule has to be explicit configuration rather than a hope."

Show `memory_user_profile_details` in [`apps/main.tf`](../apps/main.tf).

### Act 4 — prove it, and use a tool

Chips: `code_interpreter_call`, `memory_search_call`.

> "That Python was not written by me and did not run on my machine. Foundry started
> a sandbox, executed it, and read the result back. The numbers are past what a
> model does reliably in its head, so this is a real execution rather than a
> plausible guess."

Then the proof. The memories listed on the page are read from the
`memory_search_call` item in the API response — **the store's own account of
itself**, not the model's prose. Read them out and point at what is absent: no card
number, no balance, no date of birth. Only servicing preferences.

## Two questions you should expect

**"Your summary mentions a card number."** The `chat_summary` records that
sensitive data was *offered and refused*, without the values. That is a better
answer than pretending otherwise: the event stays auditable, the data is not
retained.

**"You retained 'low vision' — that is health data."** True, and deliberate.
`user_profile_details` permits accessibility needs, because an assistant that
forgets an accessibility need is worse than useless. A conscious inclusion, not an
oversight.

**"Does this affect the actual banking workflow, or just this page?"** Both, now.
A `profile` step runs ahead of the planner in `AgentFrameworkWorkflowOrchestrator`
(`ExecuteProfileStepAsync`), and what it recalls is passed to the planner and into
the specialist agent's `context` dictionary, so a stated contact or accessibility
preference can shape the plan and the wording of the answer.

Two properties are worth volunteering before anyone asks:

- **It fails open.** If the profile agent is undeployed, unreachable, slow, or
  scoped to somebody else, the workflow proceeds without personalisation. A
  customer disputing a transaction gets an answer whether or not the memory
  service is healthy. `ProfileStepFailOpenTests` pins this by running the whole
  workflow with a profile client that throws.
- **The scope is checked, not assumed.** The orchestrator calls Foundry with its
  own managed identity, so the customer scope is asserted by the application
  rather than derived from a user token. `CustomerProfileClient.EnforceScope`
  discards any memory that comes back under a different scope than the one asked
  for. If Foundry ever accepted the scope and quietly ignored it, the result is
  lost personalisation rather than one customer seeing another's details.

**"So is this multi-tenant safe?"** Be straight about this one. Sign-in is real —
Container Apps built-in authentication puts Entra in front of the Web UI, and the
signed-in object identifier travels on the workflow as `WorkflowState.CustomerId`
because workflows run in the background, long after the sign-in token is gone. But
the orchestrator still calls Foundry as itself, and the orchestrator's own ingress
is unauthenticated. This is a sound pattern — authenticate the user, then scope
their data — not a finished authorisation story. Issue #40 tracks the rest.

**"What stops it saying something harmful?"** Azure's default content filter
runs on the model deployment, screening both prompts and completions for hate,
violence, sexual content and self-harm, plus prompt-injection detection on
input. That applies to every agent here, because they all call Foundry models.

Be precise about the second layer rather than implying it exists. Foundry also
supports a guardrail attached to the *agent* itself, via `rai_config` on the
agent definition, but it requires the ARM resource ID of a Responsible AI
policy the bank has defined. No such policy exists in this deployment yet, so
that field is deliberately omitted and the model-deployment filter is the whole
story today. Backlog item 15 covers defining one as code.

Do not point at `WorkflowRoutingPolicy` when answering this. That is an
approval control, not a safety control. It is a good answer to a different
question: it can only escalate `requires_approval`, never remove it, so even a
poisoned memory cannot talk the workflow past a human approval gate.

## The closing line

Everything in the demonstration — creating the agent, calling it, reading memory,
executing the tool — ran on Microsoft Entra ID. No API keys, no connection strings.
The Foundry account has `disableLocalAuth = true`, so key-based access is not
merely unused, it is refused.

## Where the traces are

- **Foundry portal** → the project → the agent → its runs.
- **Application Insights**: the workspace is shared with the rest of the app. Note
  that the orchestrator's OpenTelemetry spans do *not* cover this agent, because
  the orchestrator does not call it.

## If it goes wrong on stage

| Symptom | Cause | What to do |
| --- | --- | --- |
| `rate_limited; retrying in Ns` | Memory extraction runs the chat model again after every turn, so a demo spends roughly double the visible tokens | The script waits it out. Keep talking; it recovers |
| Act 2 recalls nothing | Extraction had not finished | Re-run act 2: `-- --act 2`. Raise `--settle` |
| Act 2 recalls things you never said | A previous run was not cleared | `-- --reset`, then start again |
| `tool_user_error` about MCP auth | The agent has been pointed at the toolbox again | See below — the deployer guards against this |
| 404 on the agent | Agents not deployed | `task app:deploy-hosted-agents` |
| The page says the agent is not configured | `MEMORY_STORE_NAME` is empty, so `enable_agent_memory` is false | `task app:apply` with memory enabled |
| The UI shows no memories the script just stored | They are different scopes — see the warning at the top | Present from one surface only |

### The MCP trap

A prompt agent must **not** reach its tools through the project toolbox. Foundry
cannot bind a prompt agent's `mcp` tool to the agent identity — the tool's
`authorization` field is a literal header string — so the call is rejected with a
401 at invocation time, and it takes the whole response down *including the memory
tool*. Deployment still reports success, so nothing catches it until someone talks
to the agent.

The toolbox is for the hosted container agents, which authenticate to it with their
own identity from inside the container. Prompt agents declare tools inline. The
deployer now rejects an `mcp` entry in `MEMORY_AGENT_TOOLS` for this reason.

## Configuration behind the demonstration

| Setting | Where | Why |
| --- | --- | --- |
| `memory_update_delay_seconds = 0` | `apps/variables.tf` | Foundry defaults to batching extraction. At 300s a preference is not recallable for five minutes, and the demonstration silently fails |
| `capacity = 100` | `infrastructure/ai.tf` | Rate limit, not a reservation; GlobalStandard bills per token. Raised because memory extraction doubles token spend |
| `memory_agent_tools = [code_interpreter]` | `apps/main.tf` | Declared inline, not via the toolbox |
| `enable_agent_memory`, `enable_agent_toolbox` | `apps/variables.tf` | Both must be `true`, on every apply |
| `MEMORY_AGENT_NAME`, `MEMORY_STORE_NAME` | `apps/orchestrator.tf` | What the profile page needs to reach the agent. Empty when memory is disabled, which makes the page report "not configured" rather than fail obscurely |
| `user_profile_enabled`, `chat_summary_enabled` | [`deploy.py`](../src/agents/deployer/deploy.py) | Which memory types the store keeps. Both on; there is no procedural memory option |
| `default_ttl_seconds` | `deploy.py` | 30 days. Stale preferences expire without a cleanup job |
| `MEMORY_USER_PROFILE_DETAILS` | `apps/main.tf` | The retention and exclusion rule. The whole PII story is this string |
