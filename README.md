# Banking Agent Prototype

This repository contains a banking-focused agentic application built around:

- C# orchestrator agent using Microsoft Agent Framework
- Microsoft Foundry-hosted LangGraph agents exposed as MCP tools
- LiteLLM as the AI gateway for any direct model access
- Azure Container Apps deployment
- Terraform-based infrastructure

## Repository layout

- `src/orchestrator/` - C# web API orchestrator and Agent Framework entrypoint
- `src/webui/` - ASP.NET Core web UI
- `src/agents/` - Foundry Hosted Agent runtime and deployment job
- `infrastructure/` - Terraform for Azure resources (convention-over-configuration; `region` is the only input)
- `apps/` - Terraform for application identities, Container Apps, Hosted Agent deployment job, and application RBAC
- `tasks/` - Taskfile definitions for infrastructure, image builds, and deployments
- `docs/` - project constitution and specifications

## Prerequisites

- Azure CLI authenticated with `az login`
- Terraform 1.6 or later
- [Task](https://taskfile.dev/)
- .NET 8 SDK
- Python 3
- Permission to create Azure resources and role assignments in the target subscription

Copy the environment template before using Task:

```bash
cp .env.example .env
```

The examples below use `swedencentral`. The selected region must support every service in the infrastructure stack.

## 1. Create the shared Azure infrastructure

This provisions the resource group, Microsoft Foundry account and project, model deployment, Azure Container Registry, Container Apps environment, PostgreSQL, and monitoring resources.

```bash
terraform -chdir=infrastructure init -upgrade
terraform -chdir=infrastructure workspace new swedencentral || true
terraform -chdir=infrastructure workspace select swedencentral
task cloud:apply -- swedencentral
```

The root `task up` shortcut is not currently usable because `tasks/Taskfile.cloud.yml` references an undefined `infra:config` task. Use the commands above until that Taskfile target is corrected. Application tasks read the required generated values directly from the `infrastructure/` Terraform outputs.

## 2. Build and validate the code

Run the local quality checks before creating deployment images:

```bash
dotnet build -c Release
python -m compileall src/agents/python src/agents/deployer
terraform -chdir=infrastructure fmt -check
terraform -chdir=infrastructure validate
terraform -chdir=apps fmt -check
terraform -chdir=apps validate
```

Build and push all deployable images to the provisioned Azure Container Registry:

```bash
task app:build
```

The images are tagged with the first eight characters of the current Git commit and with `latest`. This builds the orchestrator, web UI, LiteLLM gateway, Hosted Agent runtime, and Hosted Agent deployer.

## 3. Deploy the applications

Initialize and apply the application Terraform stack:

```bash
task app:init
task app:apply -- swedencentral
```

This deploys the orchestrator, web UI, LiteLLM, managed identities, application RBAC, shared web UI Data Protection storage, and the manual Container Apps Hosted Agent deployment job.

Review the Terraform plan before applying when changing infrastructure:

```bash
APP_NAME=$(terraform -chdir=infrastructure output -raw APP_NAME)
IMAGE_TAG=$(git rev-parse HEAD | cut -c 1-8)

terraform -chdir=apps plan \
  -var "app_name=${APP_NAME}" \
  -var "region=swedencentral" \
  -var "image_tag=${IMAGE_TAG}"
```

## 4. Deploy the Foundry Hosted Agents

Run the managed-identity Container Apps Job after the application Terraform apply completes:

```bash
task app:deploy-hosted-agents
```

The job idempotently creates or versions these four Hosted Agents:

- `workflow-planning`
- `transaction-explanation`
- `suspicious-activity`
- `dispute-planning`

No `azd` deployment is used.

## Complete deployment shortcut

After the shared infrastructure exists, the following command builds all images, applies the application stack, and registers the Hosted Agents:

```bash
task app:deploy -- swedencentral
```

## Definition of ready

A deployment is ready only when all of the following are true:

1. Both Terraform stacks validate and the intended application plan has been applied.
2. All application images exist in ACR with the current eight-character Git commit tag.
3. The orchestrator, web UI, and LiteLLM Container Apps have healthy running revisions.
4. The Hosted Agent deployer job has completed successfully.
5. All four Hosted Agents exist in the Foundry project with an active version.
6. A workflow can be submitted from the web UI and displays a workflow result.
7. Informational workflows complete without approval, while sensitive actions enter `WaitingForApproval` and can complete through the approval form.
8. The web UI continues accepting form submissions after a revision restart or replica change.

## Destroy the environment

Destroy application resources before destroying shared infrastructure:

```bash
task app:destroy -- swedencentral
task cloud:down -- swedencentral
```

## Implementation planning

- `docs/phase-plan.md` outlines the implementation phases for the project.
- `docs/implementation-backlog.md` captures the near-term backlog and acceptance criteria.
- `docs/first-sprint-plan.md` describes the first implementation sprint and its scope.
