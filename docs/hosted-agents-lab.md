# Hosted Agents lab for Azure platform engineers

This lab turns the banking-agent sample into a guided workshop for Azure platform
engineers who need to understand how to provision, secure, and operate Microsoft
Foundry Hosted Agents in Azure.

## What participants will learn

By the end of the lab, attendees should be able to:

- explain how a C# workflow control plane coordinates multi-step agent tasks;
- describe the platform building blocks required for hosted agents in Azure;
- provision Container Apps, managed identities, Azure Container Registry, and
  supporting resources with the repository's infrastructure assets;
- wire Foundry-hosted specialists into a secure runtime with observability and
  traceability;
- identify the operational hardening steps needed before taking the pattern to a
  shared environment.

## Suggested audience

- Azure platform engineers responsible for landing zones, platform services, and
  shared developer environments;
- infrastructure engineers evaluating Azure Container Apps and Microsoft Foundry;
- solution architects who need a concrete reference architecture for hosted agents;
- developers who want to understand the platform side of agentic application
  delivery.

## Lab goals

This lab uses the repository as a working reference implementation. The teaching
focus is less about banking business logic and more about the platform concerns:

- a C# orchestrator that owns workflow state, approvals, and correlation IDs;
- hosted agents that execute specialized reasoning tasks behind a secure boundary;
- identity, RBAC, and managed identity patterns for service-to-service access;
- Azure deployment through Container Apps, ACR, PostgreSQL, and monitoring;
- observability and traceability across the workflow for operations teams.

## Lab architecture

The lab follows the architecture already implemented in this repository:

- C# orchestrator: `src/application/` and `src/api/`
- Azure infrastructure: `apps/` and `infrastructure/`
- Foundry-hosted specialists: `src/agents/python/`
- Durable workflow state: PostgreSQL-backed application services
- Telemetry: Application Insights and OpenTelemetry

## Prerequisites

Before the workshop, participants should have:

- a Microsoft Azure subscription;
- access to a Microsoft Foundry project and a model deployment;
- .NET SDK installed;
- Python 3.12 installed (for the hosted-agent tests);
- Docker Desktop or equivalent container tooling;
- Azure CLI, Terraform, and the repository task runner installed.

## Module 1: Understand the platform pattern

### Objectives

- understand the repo layout from a platform perspective;
- identify the control plane, hosted agents, and infrastructure pieces;
- map a single request to the workflow lifecycle and the Azure services involved.

### Exercises

1. Review the main documentation set in `docs/`.
2. Read the workflow lifecycle in `docs/functional-spec.md`.
3. Trace the orchestrator and hosted-agent boundary in:
   - `src/application/WorkflowService.cs`
   - `src/application/AgentFrameworkWorkflowOrchestrator.cs`
   - `src/infrastructure/FoundryMcpClient.cs`

### Discussion points

- Which responsibilities belong in the application layer versus the platform layer?
- Where are identity, RBAC, and observability enforced in this pattern?

## Module 2: Build and test the application baseline

### Objectives

- validate that the solution builds and tests cleanly;
- understand the local quality gate before Azure deployment;
- prepare for environment-specific rollout.

### Exercises

```bash
task test:all
```

That is the full local quality gate: .NET unit, contract, and E2E tests plus the
Python hosted-agent and deployer tests. To run only the .NET solution:

```bash
dotnet test banking-agent.sln -c Release
```

Optional build validation:

```bash
dotnet build -c Release banking-agent.sln
```

### Expected outcome

Participants should be able to show that the core solution is healthy before they
start targeting Azure infrastructure.

## Module 3: Deploy and secure the platform

### Objectives

- provision the Azure resources required for the sample;
- deploy the containerized services and hosted agents;
- verify the identity and access model that the platform uses.

### Exercises

```bash
task app:build
task app:deploy
```

`task app:build` tags images with the first eight characters of the current commit
SHA, so commit any local changes *before* building or the deploy will reference an
image tag that does not exist in ACR. `task app:deploy` runs `terraform apply`
without auto-approve and then registers the Hosted Agents, so expect interactive
confirmation prompts.

Verify the deployment end to end:

```bash
task app:smoke
```

### Platform checkpoints

During deployment, participants should inspect:

- the Azure Container Apps resources;
- the Container Registry images and tags;
- the managed identities and role assignments;
- the PostgreSQL-backed workflow state and the migration job;
- the Application Insights and telemetry configuration.

Security posture for the default lab deployment:

- Orchestrator workflow endpoints require Entra-issued tokens with the
  `Workflow.Invoke` app role. The Web UI obtains these tokens with managed
  identity; direct anonymous calls to workflow creation, approval, detail, and
  evidence endpoints are rejected.
- Health and readiness paths remain anonymous so Container Apps probes can call
  `/health/live` and `/health/ready`.
- The default PostgreSQL path is demo-grade: Entra-only database authentication
  is still required, but the broad Azure `AllowAzureServices` firewall rule
  admits resources from any Azure tenant at the network layer. Do not use that
  default path for regulated workloads.
- Some tenants forbid creating the service principal and `api://` identifier URI
  that service authentication depends on. In that case only, set
  `TF_VAR_enable_service_auth=false` together with
  `TF_VAR_allow_insecure_service_auth=true`. Workflow endpoints then accept
  unauthenticated callers, `/health/ready` reports `service_auth` as `Degraded`,
  and the configuration is rejected in Production. Never use it with real data.

Migration job troubleshooting:

- If the `database-migrator` Container Apps Job times out connecting to
  PostgreSQL on port 5432, first check whether the Flexible Server is stopped.
  A stopped server presents as a connection timeout from the job. Start it with
  `az postgres flexible-server start --resource-group <resource-group> --name <server-name>`.

### Expected outcome

Participants should be able to explain how the deployed services are connected and
where the platform controls are applied.

## Module 4: Inspect the MCP tool boundary and the agent graphs

### Objectives

- explain how the orchestrator discovers and invokes specialists over MCP;
- read a LangGraph agent graph and identify its nodes and conditional edges;
- understand why some agents are single-node and others branch.

### The MCP boundary

All four specialists are exposed as genuine MCP JSON-RPC 2.0 tools over the
authenticated Foundry hosted-agent endpoint. The orchestrator performs
`initialize`, discovers tools with `tools/list`, and invokes them with
`tools/call`. `FOUNDRY_MCP_TOOL_ENDPOINTS` (set in `apps/main.tf`) maps each tool
name to its agent endpoint:

| Tool | Agent |
| --- | --- |
| `workflow.plan` | `workflow-planning` |
| `transaction.explain` | `transaction-explanation` |
| `suspicious.assess` | `suspicious-activity` |
| `dispute.plan` | `dispute-planning` |

The versioned typed HTTP envelope remains only as a fallback for a tool absent
from that map. [ADR 0002](decisions/0002-mcp-sdk-vs-hand-written.md) records why
the MCP server is hand-written rather than built on the official SDK.

### Agent graph topology

Graph shape follows the decision the agent has to make, not uniformity:

| Agent | Shape |
| --- | --- |
| `workflow-planning` | single node — one classification step |
| `transaction-explanation` | single node — one explanation step |
| `suspicious-activity` | multi-node with a conditional edge |
| `dispute-planning` | multi-node with a conditional edge |

```text
dispute-planning:
  START -> extract_claim -> validate_completeness -> (conditional)
        -> request_more_info -> END
        -> assess_evidence -> draft_plan -> END

suspicious-activity:
  START -> gather_signals -> classify -> (conditional)
        -> plan_protective_action -> END
        -> explain_activity -> END
```

### Exercises

1. Read `src/agents/python/app/mcp_server.py` and find the `_AGENT_TOOLS`
   registry. Note that tool names must stay in sync with `apps/main.tf` and
   `src/orchestrator/ReadinessChecks.cs`.
2. Read `src/agents/python/app/agents/dispute.py` and trace both branches out of
   `route_on_completeness`.
3. Call `/health/ready` on the deployed orchestrator and confirm
   `foundry_configuration` is `Healthy`. That check performs a live MCP
   `tools/list` against all four agents.
4. Submit the `suspicious-information` and `suspicious-action` demo scenarios and
   compare the terminal states. Only the second requires approval.

### Discussion points

- Why does the suspicious-activity graph branch on whether an action was
  requested rather than on risk severity?
- Which safety properties belong in graph code rather than in the model prompt?
- What would tool discovery failure look like to an operator?

## Module 5: Operate and observe the workflow

### Objectives

- follow one request through planning, routing, specialist execution, and
  approval;
- inspect correlation metadata and workflow events;
- explain how the platform team would troubleshoot failures or latency.

### Exercises

1. Submit a request from the web UI or API.
2. Observe the workflow state transitions.
3. Review the workflow, evidence, and trace IDs in the persisted state.
4. Inspect Application Insights or the workspace telemetry for the workflow run.

### Discussion points

- Which parts of the workflow should be durable and which can be ephemeral?
- What should be visible in telemetry and what should remain private?
- What operational signals would the platform team monitor first?

## Module 6: Extend and harden the platform pattern

### Objectives

- introduce a new specialist agent or workflow step;
- harden the platform configuration for a shared environment;
- demonstrate how the pattern scales beyond the initial sample.

### Suggested extension ideas

- add a new specialist for account-change requests;
- add a policy-driven approval step for sensitive actions;
- move more platform controls into Terraform or environment-specific policy;
- set `TF_VAR_enable_private_networking=true` to add private Container
  Apps/PostgreSQL networking and remove the broad Azure-services PostgreSQL
  firewall rule;
- add stricter RBAC boundaries or an additional environment.

### Expected outcome

Participants should leave with a repeatable mental model for how to blueprint,
operate, and harden an Azure-hosted agentic workflow for a real platform team.

## Facilitator notes

- Keep the focus on the platform pattern rather than the banking domain.
- Use the sample prompts in `docs/demo-scenarios.md` to keep the lab grounded.
- Emphasize identity, security, traceability, and deployment boundaries rather
  than raw model prompting.
- Encourage attendees to ask which platform controls would be mandatory before
  production rollout.

## Suggested agenda

1. 15 minutes: overview and platform architecture walk-through
2. 20 minutes: local build and test
3. 35 minutes: Azure deployment, identity, and infrastructure review
4. 25 minutes: MCP boundary and agent graph walk-through
5. 25 minutes: trace, observe, and troubleshoot a workflow
6. 20 minutes: extension and hardening exercise
