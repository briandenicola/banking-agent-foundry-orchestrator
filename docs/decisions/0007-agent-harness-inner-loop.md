# ADR 0007 — Run the specialist step inside an Agent Harness

- **Status:** Proposed
- **Date:** 2026-09-14
- **Relates to:** [ADR 0001](0001-remove-litellm-gateway.md), [ADR 0003](0003-foundry-memory-prompt-agent.md), [ADR 0004](0004-foundry-toolbox-tools.md), [ADR 0006](0006-responses-api.md)
- **Amends:** `docs/project-constitution.md` (v1.1 → v1.2)

## Context

Microsoft Agent Framework shipped an [Agent Harness](https://learn.microsoft.com/en-us/agent-framework/concepts/harness):
the runtime scaffolding that drives a model-and-tool loop, manages context, and
applies approval policy. It packages several things this repository either
hand-wrote or does not have — todo tracking, plan/execute modes, automatic
context compaction, tool approval rules, OpenTelemetry — behind one factory
call on a chat client.

The workflow today is a five-step graph:

```
profile → planner → routing → specialist → terminal
```

`specialist` is the step this ADR is about. It is not a loop. It resolves one
tool name from the route, builds one parameter dictionary, makes **one** MCP
call to a Foundry-hosted LangGraph agent, and maps the reply onto the workflow
context. If that single answer is thin, nothing reconsiders it: there is no
second look, no follow-up tool call, no way for the agent to decide it needs
the customer's transaction history before it can explain a charge.

That is the gap the harness fills. The specialist reasoning itself is already
good and already lives in the LangGraph agents; what is missing is a loop
around them.

## The constraint nobody can skip past

Adopting the harness gives the C# orchestrator **its own model connection for
the first time**. That is not a detail. [ADR 0001](0001-remove-litellm-gateway.md)
rests on it as its central factual claim:

> Model calls in this system happen in exactly one place: inside the Python
> agents. The C# orchestrator makes none — there is no `IChatClient`, no
> `AzureOpenAI` client, and no chat-completion call anywhere in `src/**/*.cs`.

That was true when written and was verified again while preparing this ADR: a
search for `IChatClient`, `AsAIAgent`, `AIAgent`, and `ChatClient` across `src`
and `tests` returns nothing. A harness agent falsifies it, because a harness
*is* a chat client with a loop around it.

So this ADR cannot be read as a pure addition. It changes a premise two other
documents depend on, and both are updated accordingly rather than left to
contradict the code — which is the failure ADR 0003 already had to correct once.

## Decision

**The harness replaces the `specialist` executor and nothing else.**

The durable workflow stays the system of record. `profile`, `planner`,
`routing`, and `terminal` keep their current hand-built implementations, and
the approval gate is untouched.

### Why this boundary and not a larger one

The obvious alternative is to let the harness swallow the planner too, or to
replace the whole graph with a harness agent. Both were rejected for the same
reason: **the workflow is the audit spine.**

`WorkflowState` is persisted per step, versioned, and recoverable —
`WorkflowRecoveryWorker` exists specifically to resume a workflow whose process
died. Harness state is session-scoped and lives in memory. Handing the harness
the approval gate or the routing decision would move the parts a regulator
would ask about into a store that does not survive a restart and is not
queryable after the fact.

The harness gets the part where a loop genuinely helps — gathering evidence and
deciding whether it has enough — and none of the parts where durability is the
product.

### The harness does not absorb specialist logic

Guardrail: *"loads Microsoft Foundry-hosted LangGraph agents as MCP tools
rather than embedding their logic directly."*

This decision is consistent with that, and the distinction matters. The
LangGraph agents stay exactly where they are, deployed to Foundry, invoked over
MCP. What changes is that the MCP calls move **inside** a loop instead of being
made once by hand. The dispute, fraud, and transaction-explanation reasoning is
not reimplemented in C#; it is called more than once.

Had the harness been given its own prompt-level specialist instructions and
allowed to answer without calling the hosted agents, that would embed the
logic, and this ADR would be rejecting itself.

### Capabilities enabled

| Capability | Why |
|---|---|
| Todo provider | The supervisor-facing win. A supervisor currently sees a finished recommendation with no account of how it was reached. |
| Agent modes (plan/execute) | Already how this system thinks: produce a recommendation, touch nothing, wait for a human. |
| Compaction | A multi-round tool loop over banking evidence will outgrow the window; this is the failure that silently truncates evidence. |
| OpenTelemetry | Constitution principle 4. The harness emits agent spans natively and we already export to Application Insights. |

### Capabilities disabled, and why

| Capability | Why not |
|---|---|
| **Web search** | A banking answer grounded in an open web search is a compliance incident. Evidence comes from the customer's own records through MCP tools or it does not appear. |
| **Shell execution** | No defensible use inside a banking orchestrator. |
| **File access** | Same. There are no files this loop should read or write. |
| **Agent skills** | Filesystem-discovered capability injection is an unreviewed code path into an approval-gated workflow. |
| **File memory** | The sharp one. Customer memory already has a home — the Foundry memory store ([ADR 0003](0003-foundry-memory-prompt-agent.md)), scoped per customer and reached through the `profile` step. Harness file memory would create a **second, unscoped** memory surface writing customer notes to container-local disk under `agent-file-memory/{session}/`, which on Container Apps is both ephemeral and a PII exposure nobody asked for. |
| **Background agents** | Deferred, not rejected. Fanning the specialists out in parallel is attractive and belongs in its own change. |
| **Tool approval as the gate** | The harness's standing-approval rules are in-session. Our gate is a database row a human acts on minutes later, possibly after a restart. Harness approval may later classify *which* tools are sensitive, but it will not be the gate. |

### Todo state is derived, never authoritative

Harness todos live in session state and die with the process. They are
projected into `WorkflowEvent` rows through the existing `IWorkflowRepository`
path, so they land in the same audit trail as every other step and survive the
recovery worker. Nothing reads workflow truth back out of harness session
state.

### Model access

The orchestrator's managed identity gets `Azure AI User` on the Foundry
project, granted in `infrastructure/roles.tf`. No keys, per constitution
principle 1.

**Which client is deliberately left open.** Two options, to be settled by
verification rather than by preference:

1. `Microsoft.Agents.AI.Foundry` — the first-party path, currently
   **`1.21.0-preview.260911.1`**.
2. `Azure.AI.OpenAI` 2.1.0 with `Microsoft.Extensions.AI.OpenAI` 10.10.0 —
   both **stable**, producing an `IChatClient` against the same Foundry
   `/openai/v1` surface and Entra scope the rest of the system already uses.

Option 2 is preferred if it works, and the reason is immediate: the change that
precedes this one existed to get the solution *off* a release candidate. Taking
a preview dependency in the very next change would undo that unless the
first-party client earns it. `Microsoft.Agents.AI.Harness` itself is stable
(1.14.0 onward), so the harness does not force the preview — only the choice of
chat client does.

Option 2 also agrees with [ADR 0006](0006-responses-api.md), which standardised
this system on the Responses API.

## Consequences

**Gained**

- The specialist step can take a second look instead of being a single shot.
- A supervisor sees the agent's working, not just its conclusion.
- Compaction, tool approval plumbing, and agent-level telemetry stop being ours
  to write.

**Lost, and worth stating plainly**

- **A second model-calling component.** [ADR 0001](0001-remove-litellm-gateway.md)
  recorded that losing LiteLLM left no home for token accounting and no cost
  ceiling, and that the gap widens as agent graphs issue more calls per
  invocation. This widens it again, and in a new place: model spend now
  originates in the orchestrator as well as in the agents. It also moves
  revisit condition 3 of that ADR measurably closer, because a gateway now has
  two consumers rather than one — and unlike LiteLLM, the orchestrator sits
  inside the Container Apps Environment where an internal gateway is reachable.
  That is not a reason to reintroduce one here, but it is the first time the
  blocking constraint in ADR 0001 has softened, and the next person asking
  should find that written down.
- **A loop costs more than a single call**, in tokens and in latency, on a
  system with no ceiling.
- **Harness defaults are opinionated and will change.** Seven capabilities are
  disabled above. A future version adding an eighth, enabled by default, lands
  in an approval-gated banking workflow on upgrade. Pin the version and read
  the release notes.

## Revisit conditions

- Harness tool approval becomes durable enough to be the gate, at which point
  the in-session/durable split above should be reconsidered rather than
  inherited.
- Background agents are adopted for parallel specialist fan-out.
- Token accounting (#33) selects a gateway, at which point the orchestrator is
  now a consumer that can actually reach one.

## Constitution amendment

Guardrail, currently:

> Model access is made directly against Microsoft Foundry by the hosted agents.
> There is no AI gateway in the current architecture.

Becomes (literal replacement text, paths relative to the constitution):

```markdown
Model access is made directly against Microsoft Foundry, by the hosted agents
and by the orchestrator's harness-driven specialist loop. There is no AI
gateway in the current architecture; see [ADR 0001](decisions/0001-remove-litellm-gateway.md)
and [ADR 0007](decisions/0007-agent-harness-inner-loop.md).
```

The "no AI gateway" position is unchanged. What changes is the claim that
hosted agents are the only thing calling a model.
