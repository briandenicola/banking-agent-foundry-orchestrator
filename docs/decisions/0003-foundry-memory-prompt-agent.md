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
- **No custom memory client code.** Foundry runs the model loop and the tool.

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
