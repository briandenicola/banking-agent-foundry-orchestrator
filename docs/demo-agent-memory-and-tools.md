# Talk track: agent memory and tools in Microsoft Foundry

A ten-minute demonstration of the `customer-profile` agent, showing Foundry-managed
memory and a Foundry-managed tool working together on Entra ID alone.

There are two ways to run it. **Use the Web UI for the talk**; the script is for
rehearsal and for when you want the raw responses.

| | Web UI | Script |
| --- | --- | --- |
| How | **Customer profile agent** in the header | `task app:demo-customer-profile` |
| Good for | Presenting: buttons per act, memories on screen | Rehearsing, debugging, seeing raw JSON |
| Runs as | The orchestrator's managed identity | You, via `az login` |

> **These two do not share a memory scope.** Foundry derives the scope from the
> caller's token, so the UI writes as the orchestrator's managed identity and the
> script writes as you. Memories set in one are invisible to the other. Pick one
> and stay in it for the whole demonstration. "Clear all memories" in the UI is the
> exception: it recreates the store, which wipes both.

## Running it from the Web UI

Open **Customer profile agent** in the header. The page has a button per act, a
free-text box, and a live list of what the store retained.

1. **Clear all memories** first. See the warning below about why.
2. Press an act button — it fills the message box so the audience can read the
   prompt before you send it.
3. **Send**. The reply appears with a line naming the tools Foundry ran.
4. **Show retained memories** after a few seconds. Extraction runs *after* the
   reply, so the store lags the answer by a moment; if act 1 looks like it stored
   nothing, press it again rather than assuming a failure.


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

## Before you start

Clear the store, in whichever surface you are presenting from:

```bash
task app:demo-customer-profile -- --reset          # clear previous runs
task app:demo-customer-profile -- --show-memories  # should print (none)
```

or press **Clear all memories** on the profile page.

Reset matters more than it looks. The demonstration's whole claim is that act 2
recalls something act 1 stored. If a previous rehearsal left memories behind, act 2
will look like it works **even if act 1 failed**, and you will not find out on
stage.

## The four acts

Each act is a separate HTTP request with **no `previous_response_id`**. There is no
conversation history. That is the control: anything recalled came out of the memory
store, not out of the prompt.

### Act 1 — the customer states a preference

> "Please contact me by SMS only, never phone. I also need large-print statements
> because I have low vision."

Point at the tool line:

```
[Foundry ran: memory_command_preview_call, memory_command_preview_call_output, memory_search_call]
```

**Talk track.** "I did not write code to extract that preference. I did not write a
schema for it. Foundry decided there was something durable in that sentence and
wrote it. The only thing I configured was *what is allowed to be kept* — and we
will come back to that in act 3."

### Act 2 — a new conversation. Does it remember?

> "How should you contact me, and is there anything I need for readability?"

```
[Foundry ran: memory_search_call]
```

**Talk track.** "New request, no history. It has to go and look. Notice what it did
*not* do: it did not ask me to identify myself." Then show the scope:

In the UI the scope is printed under **Retained memories**; from the script it is
`task app:demo-customer-profile -- --show-memories`. Either way it is a resolved
identity, not a string we passed:

```
Memory scope: cd3cbaf1-…-…_16b3c013-…
```

The agent definition says `"scope": "{{$userId}}"`. Foundry substitutes the caller's
Entra object ID from the token on the request. **Per-user memory isolation is a
template variable, not application code.** Nobody can read another customer's
preferences, because no code was written that could get it wrong.

### Act 3 — the customer volunteers PII

> "My card number is 4111 1111 1111 1111, my balance is 8,412.66 dollars, and my
> date of birth is 3 March 1979. Please prefer email for marketing."

The agent keeps the marketing preference and refuses the rest.

**Talk track.** "Memory extraction is model-driven. In a banking assistant, the
conversation is saturated with exactly the data you must not retain, so the
exclusion rule is explicit configuration rather than a default." Show
`memory_user_profile_details` in [`apps/main.tf`](../apps/main.tf).

### Act 4 — prove it, and use a tool

> "What do you remember about me? Also, here are my card spends this month: … work
> out the total, the mean and the sample standard deviation …"

Two things land in one response:

```
[Foundry ran: code_interpreter_call, memory_search_call]
[code interpreter] import statistics
                   spends = [48.20, 12.99, …]
```

**Talk track, part one — the tool.** "That Python was not written by me and was not
run on my machine. Foundry spun up a sandbox, executed it, and read the result
back. The numbers are deliberately past what a model reliably does in its head, so
this is a real execution, not a plausible guess."

**Talk track, part two — the proof.** The memories printed underneath are read from
the `memory_search_call` item in the API response, **not** from the model's prose.
This is the store's own account of itself. Read the list aloud and point out what is
absent: no card number, no balance, no date of birth. Only servicing preferences.

> **Be ready for this question:** *"the summary mentions a card number was given."*
> Yes — the chat summary records that sensitive data was **offered and refused**,
> without the values. That is the honest answer and it is a better story than
> pretending otherwise: the event is auditable, the data is not retained.

> **Also be ready for:** *"you retained 'low vision', which is health data."* True,
> and deliberate: `memory_user_profile_details` permits accessibility needs, because
> an assistant that forgets an accessibility need is worse than useless. It is a
> conscious inclusion, not an oversight.

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
