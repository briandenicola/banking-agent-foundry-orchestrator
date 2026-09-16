# Agent deployment

This document explains how the repository registers its agents with Microsoft
Foundry and clarifies the roles of the deployment shell script, the Container
Apps Job, and `deploy.py`.

## Deployment flow

```text
GitHub Actions
  └─ scripts/deploy-hosted-agents.sh
       ├─ starts the agent-deployer Container Apps Job
       │    └─ the job container runs deploy.py
       │         ├─ creates or versions the hosted agents
       │         ├─ creates or reuses the optional toolbox
       │         └─ creates or reuses the optional memory agent and store
       ├─ waits for the job to succeed
       └─ grants the hosted-agent identities model invocation access
```

The shell script is the launcher and coordinator. The Python program performs
the Foundry provisioning inside the Container Apps Job.

## How the job runs `deploy.py`

Terraform defines the manual Container Apps Job in
[`apps/agent-deployer.tf`](../apps/agent-deployer.tf#L1). It gives the job a
user-assigned identity and supplies the Foundry endpoint, hosted-agent image,
model deployment, agent definitions, and optional memory and toolbox settings
as environment variables
([`apps/agent-deployer.tf:11-105`](../apps/agent-deployer.tf#L11-L105)).

The deployer image copies `deploy.py` into the container and makes it the
container's default command
([`src/agents/deployer/Dockerfile:7-10`](../src/agents/deployer/Dockerfile#L7-L10)).

The production deployment invokes the job with `az containerapp job start`
([`scripts/deploy-hosted-agents.sh:86-91`](../scripts/deploy-hosted-agents.sh#L86-L91))
and polls until it succeeds or fails
([`scripts/deploy-hosted-agents.sh:101-134`](../scripts/deploy-hosted-agents.sh#L101-L134)).

## Agents that are created

Terraform supplies four hosted-agent definitions
([`apps/main.tf:129-146`](../apps/main.tf#L129-L146)):

| Foundry agent | Hosted graph kind |
| --- | --- |
| `workflow-planning` | `workflow-planning` |
| `transaction-explanation` | `transaction-explanation` |
| `suspicious-activity` | `suspicious-activity` |
| `dispute-planning` | `dispute-planning` |

All four definitions use the same hosted-agent container image. `deploy.py`
sets `BANKING_AGENT_KIND` in each Foundry definition
([`deploy.py:166-190`](../src/agents/deployer/deploy.py#L166-L190)), and the
container uses that value to select the corresponding graph
([`hosted.py:26-27`](../src/agents/python/app/hosted.py#L26-L27)).

When memory is enabled, the deployer also creates a Foundry-managed
`customer-profile` prompt agent and its memory store. Unlike the four hosted
agents, this prompt agent is run by Foundry and does not use the hosted-agent
container image.

## Where agent creation happens

The actual Foundry API calls that create hosted agents are in
`FoundryClient.deploy()`:

- If an agent already exists, `POST /agents/{name}/versions` creates a new
  version
  ([`deploy.py:191-195`](../src/agents/deployer/deploy.py#L191-L195)).
- If the agent does not exist, `POST /agents` creates it
  ([`deploy.py:197-201`](../src/agents/deployer/deploy.py#L197-L201)).

The optional prompt agent uses the equivalent calls at
[`deploy.py:299-309`](../src/agents/deployer/deploy.py#L299-L309).

## Hosted-agent deployment behavior

For each hosted agent, `FoundryClient.deploy()`:

1. Looks for an existing version with the same image and runtime configuration
   ([`deploy.py:151-164`](../src/agents/deployer/deploy.py#L151-L164)).
2. Builds a `kind: hosted` Foundry definition with its image, compute size,
   invocation protocol, and environment variables
   ([`deploy.py:166-190`](../src/agents/deployer/deploy.py#L166-L190)).
3. Creates the agent or a new version
   ([`deploy.py:191-202`](../src/agents/deployer/deploy.py#L191-L202)).
4. Waits for the version to become `active` or `running`
   ([`deploy.py:204-215`](../src/agents/deployer/deploy.py#L204-L215)).

The existing-version comparison includes the image, agent kind, model,
project endpoint, fallback setting, invocation timeout, and toolbox name
([`deploy.py:358-391`](../src/agents/deployer/deploy.py#L358-L391)). Therefore,
rerunning the job with unchanged configuration normally reuses the current
version instead of creating another one.

## Toolbox and memory provisioning

When configured, the deployer creates or reuses:

- A shared Foundry toolbox for the hosted agents
  ([`deploy.py:218-252`](../src/agents/deployer/deploy.py#L218-L252)).
- A memory store for the `customer-profile` prompt agent
  ([`deploy.py:254-278`](../src/agents/deployer/deploy.py#L254-L278)).
- The prompt-agent version itself
  ([`deploy.py:280-347`](../src/agents/deployer/deploy.py#L280-L347)).

An empty toolbox or memory-store name disables that optional feature
([`deploy.py:602-621`](../src/agents/deployer/deploy.py#L602-L621),
[`deploy.py:696-726`](../src/agents/deployer/deploy.py#L696-L726)).

## Authentication and failure handling

`FoundryClient` authenticates with the Container Apps Job's user-assigned
managed identity
([`deploy.py:130-142`](../src/agents/deployer/deploy.py#L130-L142)). Terraform
grants that identity the `Foundry Project Manager` role
([`apps/roles.tf:33-38`](../apps/roles.tf#L33-L38)).

All Foundry calls pass through `_request()`, which obtains a managed-identity
token, sends the REST request, and retries transient authorization, conflict,
throttling, network, and server errors
([`deploy.py:435-487`](../src/agents/deployer/deploy.py#L435-L487)).

The top-level sequence is in
[`deploy.py:729-757`](../src/agents/deployer/deploy.py#L729-L757). An exception
is logged and re-raised
([`deploy.py:760-765`](../src/agents/deployer/deploy.py#L760-L765)), producing a
nonzero container exit code so the Container Apps Job and deployment workflow
report failure.

After the job succeeds, the shell script finds each hosted agent's runtime
identity and grants it `Cognitive Services OpenAI User` access
([`scripts/deploy-hosted-agents.sh:12-83`](../scripts/deploy-hosted-agents.sh#L12-L83)).
