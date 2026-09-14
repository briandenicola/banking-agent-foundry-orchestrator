# ADR 0007 — Run the specialist step inside an Agent Harness

- **Status:** Proposed
- **Date:** 2026-09-14
- **Supersedes:** [ADR 0001](0001-remove-litellm-gateway.md)
- **Relates to:** [ADR 0003](0003-foundry-memory-prompt-agent.md), [ADR 0004](0004-foundry-toolbox-tools.md), [ADR 0006](0006-responses-api.md)
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

## What this supersedes

Adopting the harness gives the C# orchestrator **its own model connection for
the first time**. That is not a detail: it changes a premise other documents
depend on, so the constitution and ADR 0001 are updated here rather than left
to contradict the code — the failure ADR 0003 already had to correct once.

[ADR 0001](0001-remove-litellm-gateway.md) is superseded, because its reasoning
no longer describes this system. Its central factual claim was verified again
while preparing this ADR — `IChatClient`, `AsAIAgent`, `AIAgent`, and
`ChatClient` across `src` and `tests` return zero hits — and this decision is
what falsifies it:

> Model calls in this system happen in exactly one place: inside the Python
> agents. The C# orchestrator makes none.

A harness *is* a chat client with a loop around it, so from here the
orchestrator makes model calls too.

**Two things carry forward unchanged, and must not be read as reopened:**

1. **LiteLLM stays removed.** Nothing here asks for it back.
2. **There is still no AI gateway**, and introducing one still requires its own
   ADR, a consumer, and a test proving a model call traverses it — the
   discipline ADR 0001 established after a gateway was deployed with no callers.

What changes is the *premise*. ADR 0001 argued no gateway was reachable because
the only model callers were hosted agents on the far side of a network
boundary. That argument no longer holds: the orchestrator runs inside the
Container Apps Environment, so a second model caller now exists on the
reachable side. This ADR does not act on that, but it is the first time the
constraint has moved, and the next person weighing a gateway should start here
rather than from ADR 0001's network argument.

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

**The client is `Microsoft.Agents.AI.Foundry` 1.5.0.** That is the first-party
path and it is stable. The version needs explaining, because the package's
history is not monotonic: it shipped stable through `1.0.0` → `1.5.0` and then
went **back** to preview from `1.6.0` onward, so the newest version
(`1.21.0-preview.260911.1`) is preview while `1.5.0` is not. The latest release
is therefore the wrong choice here and the newest *stable* release is 1.5.0.

`Microsoft.Agents.AI.Harness` is independently stable from 1.14.0 onward, and
`Microsoft.Agents.AI [1.5.0, )` is a minimum, not an exact pin, so Foundry
1.5.0 coexists with the 1.21.0 packages already in the graph.

### This drags `Azure.AI.Projects` with it, and that was verified rather than assumed

`Microsoft.Agents.AI.Foundry` 1.5.0 requires `Azure.AI.Projects [2.0.0, )`.
This repository pins `2.0.0-beta.2`, and `2.0.0-beta.2` sorts **below**
`2.0.0`, so adding the package fails the build outright:

```
error NU1605: Detected package downgrade: Azure.AI.Projects from 2.0.0 to 2.0.0-beta.2
```

Moving to `2.0.0` then breaks the memory-store code this repository adopted in
[ADR 0003](0003-foundry-memory-prompt-agent.md):

```
error CS0246: The type or namespace name 'AIProjectMemoryStoresOperations' could not be found
```

That looks like the feature was withdrawn. It was not — it was **renamed and
moved**:

| 2.0.0-beta.2 | 2.0.0 |
| --- | --- |
| `Azure.AI.Projects.AIProjectMemoryStoresOperations` | `Azure.AI.Projects.Memory.AIProjectMemoryStores` |

`DeleteScopeAsync` — the operation ADR 0003 adopted the SDK *for*, because it
is what stopped **Clear all memories** wiping every customer's scope — survives
intact. The migration is a type rename plus a `using Azure.AI.Projects.Memory;`
in `CustomerProfileClient` and `CustomerProfileClearScopeTests`.

This was proven on a scratch branch before being written down: full solution
builds with 0 errors and `task test:infrastructure` passes 66/66, including
`CustomerProfileClearScopeTests`.

**This is a net improvement, not a cost.** It moves `Azure.AI.Projects` from a
**beta** to a **stable** release, which is the same direction of travel as the
change that precedes this one. The `AAIP001` experimental suppression in
`infrastructure.csproj` should be re-examined once the types are no longer
preview.

One thing deliberately not taken: `MemorySearchPreviewTool` — absent from
2.0.0-beta.2, which is why ADR 0003 hand-writes `BuildScopedRequest` and
`EnforceScope` — is still absent from `2.0.0` stable and reappears only in
`2.1.0-beta.4`. ADR 0003 asked that this be re-checked "in case the type
returns under a new name". It has returned, but only in beta, so the inline
scope stays hand-written and we stay on stable. That trade should be revisited
when 2.1.0 goes stable.

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
  originates in the orchestrator as well as in the agents. See *What this
  supersedes* for why the gateway question is now open on different grounds
  than ADR 0001 left it.
- **A loop costs more than a single call**, in tokens and in latency, on a
  system with no ceiling.
- **A forced dependency migration.** `Azure.AI.Projects` moves beta.2 → 2.0.0
  and `CustomerProfileClient` changes with it. Verified, mechanical, and in the
  direction of stability — but it is not optional, and it touches the memory
  feature rather than the harness.
- **Harness defaults are opinionated and will change.** Seven capabilities are
  disabled above. A future version adding an eighth, enabled by default, lands
  in an approval-gated banking workflow on upgrade. Pin the version and read
  the release notes.
- **`Microsoft.Agents.AI.Foundry` is pinned behind its own latest release.**
  1.5.0 is stable; everything after it is preview. Upgrading means either
  waiting for the line to re-stabilise or accepting a preview dependency, and
  that choice should be made deliberately rather than by a routine bump.

## Revisit conditions

- Harness tool approval becomes durable enough to be the gate, at which point
  the in-session/durable split above should be reconsidered rather than
  inherited.
- Background agents are adopted for parallel specialist fan-out.
- Token accounting (#33) selects a gateway, at which point the orchestrator is
  now a consumer that can actually reach one.
- `Microsoft.Agents.AI.Foundry` returns to a stable line above 1.5.0.
- `Azure.AI.Projects` 2.1.0 goes stable, bringing `MemorySearchPreviewTool`
  with it, at which point ADR 0003's hand-written `BuildScopedRequest` and
  `EnforceScope` may finally be replaceable.

## Constitution amendment

Guardrail, currently:

> Model access is made directly against Microsoft Foundry by the hosted agents.
> There is no AI gateway in the current architecture.

Becomes (literal replacement text, paths relative to the constitution):

```markdown
Model access is made directly against Microsoft Foundry, by the hosted agents
and by the orchestrator's harness-driven specialist loop. There is no AI
gateway in the current architecture; see [ADR 0007](decisions/0007-agent-harness-inner-loop.md),
which supersedes the reasoning in [ADR 0001](decisions/0001-remove-litellm-gateway.md).
```

The "no AI gateway" position is unchanged. What changes is the claim that
hosted agents are the only thing calling a model.
