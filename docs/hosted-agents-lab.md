# Hosted Agents lab for Azure

This lab turns the banking-agent sample into a guided workshop for teaching
Microsoft Foundry Hosted Agents, LangGraph-style agentic orchestration, and a
C#/.NET control plane.

## What participants will learn

By the end of the lab, attendees should be able to:

- explain how a C# orchestrator can coordinate multi-step agent workflows;
- describe the difference between a workflow control plane and a specialist agent;
- deploy Microsoft Foundry-hosted agents to Azure and invoke them securely;
- trace a single workflow request across orchestration, approvals, and telemetry;
- extend the sample with a new specialist or a new workflow step.

## Suggested audience

- .NET developers who want to learn agentic application patterns;
- solution architects evaluating Microsoft Foundry Hosted Agents;
- platform engineers who need a reference implementation for Azure deployment;
- educators who want a realistic but compact lab for hands-on learning.

## Lab goals

This lab uses the repository as a working reference implementation. The teaching
focus is not on banking business logic itself, but on the platform patterns:

- a C# orchestrator that owns workflow state and approvals;
- hosted agents that execute specialized reasoning tasks;
- a typed request/result contract between the orchestrator and the agents;
- Azure deployment through Container Apps and supporting infrastructure;
- observability and traceability across the workflow.

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
- Docker Desktop or equivalent container tooling;
- Azure CLI, Terraform, and the repository task runner installed.

## Module 1: Explore the reference implementation

### Objectives

- understand the repo layout;
- identify the control plane, hosted agents, and infrastructure pieces;
- map a single request to the workflow lifecycle.

### Exercises

1. Review the main documentation set in `docs/`.
2. Read the API and workflow lifecycle flow in `docs/functional-spec.md`.
3. Trace the orchestrator and hosted-agent boundary in:
   - `src/application/WorkflowService.cs`
   - `src/application/AgentFrameworkWorkflowOrchestrator.cs`
   - `src/infrastructure/FoundryMcpClient.cs`

### Discussion points

- Why is the workflow control plane in C# while the specialists are hosted as
  separate agents?
- Where do approvals and evidence attach to the workflow lifecycle?

## Module 2: Build and test the sample locally

### Objectives

- validate that the solution builds and tests cleanly;
- understand what the local quality gate looks like;
- prepare for deployment.

### Exercises

```bash
dotnet test banking-agent.sln -c Release
```

Optional build validation:

```bash
dotnet build -c Release banking-agent.sln
```

### Expected outcome

Participants should be able to show that the core solution is healthy before they
start targeting Azure.

## Module 3: Deploy the sample to Azure

### Objectives

- provision the Azure resources required for the sample;
- deploy the containerized services and hosted agents;
- confirm that the application is reachable.

### Exercises

```bash
task app:build
task app:deploy
```

### Expected outcome

Participants should be able to explain how the deployed services are connected:

- the web UI and orchestrator;
- the hosted agent image registrations;
- the PostgreSQL-backed workflow state;
- the observability resources.

## Module 4: Trace a workflow end to end

### Objectives

- follow one user request through planning, routing, specialist execution, and
  approval;
- inspect the correlation metadata and workflow events;
- explain how the system behaves when a specialist returns a recommendation or a
  failure.

### Exercises

1. Submit a request from the web UI or API.
2. Observe the workflow state transitions.
3. Review the workflow, evidence, and trace IDs in the persisted state.
4. Inspect Application Insights or the workspace telemetry for the workflow run.

### Discussion points

- Which parts of the workflow should be durable and which can be ephemeral?
- What should be visible in telemetry and what should remain private?

## Module 5: Extend the pattern

### Objectives

- introduce a new specialist agent;
- wire it into the orchestrator;
- demonstrate how the pattern scales beyond the initial sample.

### Suggested extension ideas

- add a new specialist for account-change requests;
- add a policy-driven approval step for sensitive actions;
- add a new prompt contract and test fixture for a new agent role;
- add a new Azure deployment target for a second environment.

### Expected outcome

Participants should leave with a repeatable mental model for how to turn a single
agent into a multi-step, approval-aware, Azure-hosted workflow.

## Facilitator notes

- Keep the focus on the orchestration pattern rather than the banking domain.
- Use the sample prompts in `docs/demo-scenarios.md` to keep the lab grounded.
- Encourage attendees to compare the C# orchestrator with the hosted-agent
  runtime so they see where each layer provides value.
- Emphasize security, traceability, and approval boundaries rather than raw model
  prompting.

## Suggested agenda

1. 15 minutes: overview and architecture walk-through
2. 20 minutes: local build and test
3. 30 minutes: Azure deployment and hosted-agent registration
4. 25 minutes: trace and observe a workflow
5. 20 minutes: extension exercise
