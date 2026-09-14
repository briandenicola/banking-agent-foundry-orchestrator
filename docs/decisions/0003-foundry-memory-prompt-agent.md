# ADR 0003 — Adopt Foundry memory through a dedicated prompt agent

- **Status:** Accepted
- **Date:** 2026-08-06

## Context

Before this change, no part of the system used Microsoft Foundry's memory or
tool features. That is worth stating plainly, because two things in the
repository look like they might be:

- `src/agents/deployer/deploy.py` sets `"memory": "1Gi"` in the hosted-agent
  definition. That is **container RAM**, not Foundry memory.
- The repository has an MCP server and MCP tools. Those are **our own**
  JSON-RPC tools, called by the C# orchestrator. Foundry has no knowledge of
  them and they are not registered as Foundry tools.

The four existing agents (`workflow-planning`, `transaction-explanation`,
`suspicious-activity`, `dispute-planning`) are `kind: hosted` container agents.
They declare no `tools` array and hold no state between invocations.

## The constraint that decided the design

Foundry's memory search tool attaches to agents declaratively:

```json
{ "kind": "prompt", "tools": [{ "type": "memory_search_preview", ... }] }
```

The documented attachment path is for **prompt** agents. Memory is also
extracted from *conversation turns*, governed by an `update_delay`.

Our hosted agents have no conversations. The C# orchestrator invokes each one
exactly once per workflow step with a self-contained `AgentRequest`, and the
graph runs `ainvoke` and returns. There is no thread for Foundry to observe, so
the declarative path does not map onto them. A hosted agent could only use
memory by calling the low-level memory APIs itself and passing an explicit
`scope`.

## Decision

Add a fifth agent, `customer-profile`, as a **`kind: prompt`** agent with the
`memory_search_preview` tool attached.

Consequences of note:

- **The four hosted agents are unchanged.** Their audit-critical routing,
  approval gating, and evidence rules stay deterministic and memory-free.
  Nothing that decides whether a request requires approval can be influenced by
  remembered content.
- **Memory is not in the workflow decision path.** `customer-profile` is a
  servicing/guidance agent. It cannot approve or action anything.
- **Little custom memory client code.** Foundry runs the model loop and the
  tool, so there is no retrieval, embedding, or prompt-assembly code.

> **Amendment (later).** "The four hosted agents are unchanged" and
> "memory-free" were true when written and are now too strong. Remembered
> content does reach the specialists: a stored contact preference is resolved
> against what the deployment can service and arrives in the specialist context
> as `contact_channel_guidance`, and the fallback path was changed so the same
> refusal survives an outage. This was necessary because a recalled preference
> that only travels as text is worse than no recall at all — an agent that reads
> "only contact me by SMS" agrees to it, and nothing can send an SMS.
>
> The claim that actually matters is unchanged and is now pinned by a test
> rather than asserted in prose: contact capability affects wording and one
> audit event, never `requires_approval` and never the route.
> `ContactChannelWorkflowTests` asserts that in both directions, because a
> policy that could only escalate approval would still have broken the property
> this ADR is defending. The de-escalation case runs a dispute rather than an
> informational request: with an informational one, approval is false whether or
> not the feature misbehaves, so the test would have passed by asserting that
> false equals false.

> **Correction (later).** "No custom memory client code", as this ADR first
> put it, did not survive contact with the service.
> `CustomerProfileClient` hand-writes `BuildScopedRequest` and `EnforceScope`
> to work around scope being ignored next to an `agent_reference`, and `Parse`
> to read memories out of the `memory_search_call` item. What it no longer
> hand-writes is the memory *store* calls: those moved to
> `Azure.AI.Projects` (see "Rejected: Agent Framework's memory abstractions"
> below).

### Rejected: Agent Framework's memory abstractions

Agent Framework is the orchestration library for this project, so its memory
surface is the obvious first candidate and is worth explaining rather than
passing over.

In C# (`Microsoft.Agents.AI` 1.0.0-rc5) that surface is `AgentSession`,
`InMemoryChatHistoryProvider`, `ChatHistoryMemoryProvider`, and
`AIContextProvider`. All four are **client-side conversation history and
context injection**: they persist and replay the messages of a thread, and let
you push extra instructions into a run. Foundry memory is a different thing —
**server-side semantic extraction**, where a second model pass decides what is
worth keeping, embeds it, and reconciles it with what is already stored. One is
not a substitute for the other, and adopting Agent Framework's would mean
building the extraction, embedding, redaction, and expiry ourselves.

Two further reasons, either of which would be decisive alone:

- **No hook for the scope override.** Agent Framework composes the outbound
  request, and exposes no per-request seam to rewrite the memory tool's
  `scope`. Since that rewrite is the only thing that makes memory per-customer
  here, adopting the abstraction would silently return the system to a single
  shared scope — and the failure is invisible, because the write succeeds and
  simply lands where nothing reads.
- **`AgentSession` introduces thread continuity**, which would destroy the
  property that makes recall demonstrable at all: no turn carries
  `previous_response_id`, so anything the agent recalls provably came from the
  store rather than from the prompt.

What *was* adopted is the Azure SDK rather than the framework.
`Azure.AI.Projects` 2.0.0-beta.2 — already in the dependency graph beneath
`Microsoft.Agents.AI.AzureAI` — exposes `AIProjectClient.MemoryStores`, and`DeleteScopeAsync` on it removes a genuine defect: clearing memories used to
delete and recreate the whole store, wiping every customer's scope, because
per-item deletion is rejected for the identifiers memory search returns. The
Responses call stays hand-written, because `MemorySearchPreviewTool` exists in
2.0.0-beta.1 and is **absent** from 2.0.0-beta.2, leaving no typed way to
attach or rewrite the memory tool on a definition. These types are also marked
experimental (`AAIP001`), which `src/infrastructure/infrastructure.csproj`
suppresses deliberately.

> **Correction (later).** `Azure.AI.Projects` is no longer "already in the
> dependency graph beneath `Microsoft.Agents.AI.AzureAI`". When the repository
> moved to Microsoft Agent Framework 1.21.0, `Microsoft.Agents.AI.AzureAI`
> 1.0.0-rc5 was found to have no code referencing it at all — it was pinning
> the solution to a release-candidate purely to supply this transitive
> dependency — and was removed. `Azure.AI.Projects` is now a standalone direct
> reference, which is what the memory-store code actually needs.

### Rejected: memory on `suspicious-activity`

Attractive as a banking narrative ("has this customer reported fraud before?"),
but it would have required hand-written memory API calls inside the container
*and* it would put remembered, model-extracted content into a fraud decision
path. Rejected on both counts for now.

## Privacy controls

Memory extraction is model-driven, so the only durable control over what is
retained is the `user_profile_details` instruction. Banking conversations are
dense with exactly the data we must not keep, so this is set deliberately in
`apps/main.tf` rather than left to a default, and the deployer **fails** if it
is unset or blank (`_memory_agent()`).

- `scope` is `{{$userId}}`, so memory is partitioned per end user. A static
  scope would pool every customer's memories into one collection. A test
  asserts this, and asserts that changing it forces a new agent version.
- `default_ttl_seconds` is 30 days.
- The instruction forbids retaining account numbers, card numbers, balances,
  transaction amounts, government identifiers, credentials, precise location,
  date of birth, and age.

These are mitigations, not guarantees: extraction is probabilistic. Memory must
not be treated as an audit record, and the memory store is not the system of
record for anything.

## Infrastructure impact

Memory stores require an **embedding** model deployment in addition to the chat
model. The project previously deployed only `gpt-5.4-mini`, so
`text-embedding-3-small` (v1, `GlobalStandard`) is added in
`infrastructure/ai.tf`, serialized behind the chat deployment because Cognitive
Services rejects concurrent deployment writes on one account.

`GlobalStandard` is not a stylistic choice: `az cognitiveservices model list`
shows only `GlobalStandard` and `DataZoneStandard` for this model — plain
`Standard` is not offered.

The **Foundry User** role on the project's managed identity is a memory
prerequisite and already exists (`apps/roles.tf`).

## Preview status

Memory in Foundry Agent Service and the Memory Store API are **preview**,
served under api-version `2025-11-15-preview` (the agents API remains `v1`).
Preview terms in the Microsoft Product Terms apply. This is acceptable for a
demonstration environment and should be re-evaluated before any production use.

## Reversibility

The feature is off by default and gated by the `enable_agent_memory` Terraform
variable:

```bash
ENABLE_AGENT_MEMORY=true task app:apply -- <region>
task app:deploy-hosted-agents
```

When the flag is false, `local.memory_store_name` resolves to an empty string,
the deployer skips the memory store and the prompt agent entirely, and the four
hosted agents deploy exactly as before. An environment without an embedding
deployment therefore still works.

> **Correction.** As first shipped, this was opt-in only in the deployer:
> `apps/` set `MEMORY_STORE_NAME` unconditionally, so any `app:apply` enabled
> memory whether or not that was intended. The Terraform flag above closes that
> gap, and `scripts/tests/test_agent_feature_flags.py` pins it so the claim and
> the deployment cannot drift apart again.

## Verification status

**Partially verified against live Azure.** A `swedencentral` deployment with
`ENABLE_AGENT_MEMORY=true` created the memory store and registered the
`customer-profile` prompt agent, and `app:deploy-hosted-agents` completed
successfully.

What that run did not exercise: no workflow invoked `customer-profile`, so
memory extraction, the `scope = {{$userId}}` partitioning, the 30-day
retention, and the `user_profile_details` redaction instruction have not been
observed against real conversations. Treat the redaction behaviour as
unverified until it is.
