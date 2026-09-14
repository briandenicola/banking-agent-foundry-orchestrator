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

**No new role assignment is needed**, and the first draft of this ADR got that
wrong. It asked for `Azure AI User` in `infrastructure/roles.tf`, which is
wrong three ways: the file is `apps/roles.tf`, the role is unnecessary, and the
access already exists. The orchestrator's managed identity already holds
`Cognitive Services User` on the Foundry account, whose data actions are the
wildcard `Microsoft.CognitiveServices/*`, and `CustomerProfileClient` already
reaches `{endpoint}/openai/v1/responses` with that identity today. The harness
agent calls the same API on the same endpoint as the same caller, so there is
nothing left to grant.

Still no keys, per constitution principle 1.

**The client is `Microsoft.Agents.AI.Foundry` 1.5.0.** That is the first-party
path and it is stable. The version needs explaining, because the package's
history is not monotonic: it shipped stable through `1.0.0` → `1.5.0` and then
went **back** to preview from `1.6.0` onward, so the newest version
(`1.21.0-preview.260911.1`) is preview while `1.5.0` is not. The latest release
is therefore the wrong choice here and the newest *stable* release is 1.5.0.

`Microsoft.Agents.AI.Harness` is independently stable from 1.14.0 onward, and
`Microsoft.Agents.AI [1.5.0, )` is a minimum, not an exact pin, so Foundry
1.5.0 coexists with the 1.21.0 packages already in the graph. That 16-version
gap is wider than it looks comfortable being, so it was tested rather than
trusted: a runtime probe resolved `ProjectResponsesClient` →
`AsIChatClientWithStoredOutputDisabled` → `AsHarnessAgent` → `HarnessAgent`
against Foundry 1.5.0 with `Microsoft.Agents.AI` 1.21.0, and confirmed the
client is Responses-based, which is what [ADR 0006](0006-responses-api.md)
requires.

**This pin is "newest stable", not "newest".** Two alternatives were built and
tested, and both pass equally — the choice is about release quality, not
function:

| Foundry | `Azure.AI.Projects` | Build / infra tests |
| --- | --- | --- |
| **1.5.0** (chosen) | **`2.0.0` — stable** | 0 errors, 66/66 |
| 1.20.0-preview | `2.1.0-beta.4` | 0 errors, 66/66 |
| 1.21.0-preview | `3.0.0-beta.2` | 0 errors, 66/66 |

Only `1.21.0-preview` crosses to the `Azure.AI.Projects` 3.x line; everything
from 1.6.0 to 1.20.0-preview sits on `2.1.0-beta.x`. So the real question was
whether to take a preview Foundry *and* a beta `Azure.AI.Projects` in order to
close the version gap. We take the stable pair instead. The gap is proven to
work, and nothing in this ADR's scope needs a preview-only capability.

Revisit when `Microsoft.Agents.AI.Foundry` returns to stable above 1.5.0.

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
change that precedes this one.

The `AAIP001` suppression in `infrastructure.csproj` **stays**, and the
tempting assumption that a stable package makes it removable was tested and is
false: deleting the `<NoWarn>` against `2.0.0` stable produces four
`error AAIP001`. The memory types are still `[Experimental]`. A stable package
is not the same thing as a stable API, and the same holds for
`AsIChatClientWithStoredOutputDisabled`, which is gated behind `MAAI001` even
in Foundry 1.5.0 stable. Expect to suppress both.

### Memory search is unaffected, and gets a better path

`MemorySearchPreviewTool` is absent from `2.0.0` stable and returns only in
`2.1.0-beta.4`. ADR 0003 asked for this to be re-checked "in case the type
returns under a new name", so the check was done — and the finding is that the
question does not bite, because **nothing in this repository references that
type**. Memory search is attached and read as raw JSON on both sides:

```
src/agents/deployer/deploy.py:85                 "type": "memory_search_preview"
src/infrastructure/CustomerProfileClient.cs:51   const string MemoryToolType = "memory_search_preview";
scripts/verify-memory-scope.py:205               tool.get("type") == "memory_search_preview"
```

`MemorySearchPreviewTool` is a *tool-attachment* type — it declares the tool on
an agent definition from C#. This repository declares it from Terraform and
`deploy.py`, in JSON. Its absence has therefore cost nothing since
`2.0.0-beta.2`, and costs nothing here. Verified: all 31 `CustomerProfile`
tests pass on the 1.5.0 / `2.0.0` pair, inline scope rewriting included.

What `2.0.0` stable *adds* is a typed, directly scoped search:

```csharp
Task<...> SearchMemoriesAsync(string memoryStoreName, MemorySearchOptions options, CancellationToken ct)
MemorySearchOptions(string scope)   // scope is a required constructor argument
```

This matters more than it first appears. The reason ADR 0003 hand-writes
`BuildScopedRequest` and `EnforceScope` is that Foundry **silently ignores**
`scope` when it sits next to an `agent_reference`: the call returns 200 and the
write lands where nothing reads. `SearchMemoriesAsync` never touches an agent
definition, so there is nothing to ignore it, and `scope` is a constructor
argument rather than an optional field — it cannot be forgotten.

That makes it the natural way to give the harness agent memory search: an
`AIFunction` bound to `SearchMemoriesAsync`, with the scope supplied from the
workflow's customer identity. Whether to adopt it is **out of scope for this
ADR** and is not required by the harness change; it is recorded here so the
option is not rediscovered later.

### Noted, deliberately not decided here

Foundry 1.5.0 also ships `FoundryMemoryProvider : AIContextProvider` with
`FoundryMemoryProviderScope(string scope)`, plus a `Redactor` and `MaxMemories`
on its options. [ADR 0003](0003-foundry-memory-prompt-agent.md) rejected Agent
Framework's memory abstractions partly because there was **"no hook for the
scope override"**. That hook now exists — session-scoped rather than
per-request, so it fits one-session-per-customer and not arbitrary per-call
scoping.

This reopens an ADR 0003 rejection and deserves its own decision rather than
being smuggled in through a harness ADR. Tracked separately; not adopted here.

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
- `Microsoft.Agents.AI.Foundry` returns to a stable line above 1.5.0, at which
  point the version table above should be re-run.
- The typed `SearchMemoriesAsync` path is adopted, at which point ADR 0003's
  hand-written `BuildScopedRequest` and `EnforceScope` become removable. Note
  this does *not* depend on `MemorySearchPreviewTool` returning to stable —
  that type attaches tools from C#, which this repository does not do.
- `FoundryMemoryProvider` is evaluated against ADR 0003's scope-hook rejection.

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
