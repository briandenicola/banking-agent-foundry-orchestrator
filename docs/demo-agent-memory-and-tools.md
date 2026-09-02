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

**Scoping.** The tool declares `"scope": "{{$userId}}"`, which Foundry substitutes
from the caller's Entra token. There is no application code that could get this
wrong, and no customer identifier is passed in the request — we probed this: a
`user` field on the request is silently ignored, and the scope stays bound to the
token. Per-user isolation is a template variable.

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

Then point at the scope line under the memories. It is a resolved Entra object ID,
not a string the application passed:

```
Scope cd3cbaf1-…_16b3c013-… — resolved by Foundry from the caller's Entra token.
```

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
