# ADR 0001 — Remove the LiteLLM gateway

- **Status:** Accepted
- **Date:** 2026-08-06
- **Issue:** [#24](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/24)
- **Amends:** `docs/project-constitution.md` (v1.0 → v1.1)

## Context

The constitution required LiteLLM as the AI gateway:

> Use LiteLLM as the AI gateway for provider abstraction, retries, and routing where direct model access is needed.

LiteLLM was provisioned accordingly: a Container App with `min_replicas = 1`, a dedicated user-assigned managed identity, `AcrPull` and `Cognitive Services User` role assignments, an image rebuilt on every `task app:build` and on every CI run, and internal ingress inside the Container Apps Environment.

It never served a single request.

#24 proposed two options: wire it up (preferred in the issue) or remove it. Investigation showed the preferred option is not achievable in the current architecture.

## The blocking constraint

Model calls in this system happen in exactly one place: inside the Python agents. The C# orchestrator makes none — there is no `IChatClient`, no `AzureOpenAI` client, and no chat-completion call anywhere in `src/**/*.cs`. It only invokes agents over Foundry hosted-agent endpoints.

Those agents do not run in our Container Apps Environment. They are deployed to Microsoft Foundry as hosted agents:

```python
# src/agents/deployer/deploy.py:68
"container_configuration": {"image": image},
```

They therefore execute in Foundry's compute, on the other side of a network boundary from our environment. LiteLLM used internal ingress (`external_enabled = false`), reachable only from inside the Container Apps Environment.

So the only possible consumers could not reach the gateway, and the only component that could reach it had nothing to send.

Making it reachable would require one of:

1. **Public ingress on LiteLLM.** Rejected. It directly contradicts [#30](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/30) and [#40](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/40), and the tenant cannot provision the service principal needed to authenticate it.
2. **Foundry BYO VNet with agent runtime injection.** Foundry supports this: with a capability host and a delegated subnet, hosted-agent egress follows the customer VNet and can reach private endpoints. But VNet injection is a **create-time** property of the Foundry project — it cannot be enabled on an existing project, so adopting it means recreating the Foundry account and project. Our Foundry resources have no capability host and no VNet injection today; `enable_private_networking` covers only Container Apps and PostgreSQL ([#27](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/27)), not Foundry.

Option 2 is legitimate but is a far larger change than #24, and it belongs with the private-networking work rather than with a gateway cleanup.

## Decision

Remove LiteLLM: `apps/litellm.tf`, `src/litellm/`, its managed identity, both role assignments, the `LITELLM_INTERNAL_FQDN` output, the unused `litellm_internal_url` local, the `build-litellm` task, and the image build steps in CI and the production deploy workflow.

Amend the constitution so it no longer mandates a component the architecture cannot currently use.

## Consequences

**Gained**

- No compute cost for an unreachable service.
- Two RBAC grants removed, including `Cognitive Services User` on the Foundry account for an identity with no consumer.
- One fewer image built on every local build, every CI run, and every production deploy.
- The documented architecture now matches the deployed one. Readers using this repository as a reference are no longer told that model traffic is centralised through a gateway when it is not.

**Lost, and worth stating plainly**

- The natural home for token accounting, per-agent model routing, and centralised rate limiting is gone. That makes [#33](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/33) harder, not easier: those controls now have to be implemented inside the agents or at the Foundry layer.
- This matters more once [#22](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/22) lands, because multi-node agent graphs issue one model call per node rather than one per invocation, multiplying spend on a system that currently has no cost ceiling.

Removing an unused service is still the right call — an unreachable gateway provides no cost control either. But the gap it leaves is real and is tracked in #33.

## Revisit conditions

Reintroduce a gateway when **any** of the following becomes true:

1. Foundry is redeployed with BYO VNet and a capability host, making a private in-VNet gateway reachable from hosted agents (pairs naturally with #27).
2. A second model provider or a fallback path is genuinely required.
3. #33 selects a gateway as the implementation for token accounting and rate limiting, and a reachable placement exists for it.

If it is reintroduced, it must ship with a consumer and a test proving a model call traverses it, in the same change. The failure mode this ADR corrects was deploying the gateway first and expecting callers to arrive later.
