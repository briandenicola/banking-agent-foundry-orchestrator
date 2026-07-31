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
- .NET 10 SDK
- Python 3
- Permission to create Azure resources and role assignments in the target subscription
- Entra permission to create application registrations, service principals, and app-role assignments
  only when optional service authentication is enabled

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

This deploys the orchestrator, web UI, LiteLLM, managed identities, application RBAC, and the manual database migration and Hosted Agent deployment jobs. For the MVP, the web UI runs one replica and keeps ASP.NET Data Protection keys in local container storage because subscription policy disables public Storage access. Replacing the web UI replica invalidates existing antiforgery cookies.

Service-to-service API authentication is disabled by default so deployment does not require Entra directory administration. To enable it in a tenant where the deployer has suitable directory permissions, set `TF_VAR_enable_service_auth=true` before planning and applying the `apps/` stack.

Run the Entra-authenticated database migration job before sending application traffic:

```bash
task app:migrate
```

The job applies EF Core migrations and grants the orchestrator managed identity runtime-only access to the application tables. Database schema administration remains isolated from the orchestrator.

Dispute workflows can include up to five supporting PDF, PNG, JPG, or JPEG files of
10 MB each. Evidence content, validated metadata, and SHA-256 hashes are stored in
PostgreSQL with the workflow rather than Azure Storage, avoiding public-network
storage dependencies. Uploaded evidence is available from the durable workflow view.

API failures use RFC-compatible `application/problem+json` responses with a stable
`code` and request `traceId`. Boundary validation returns `validation_failed`;
missing resources return `workflow_not_found` or `evidence_not_found`; state
conflicts return `workflow_conflict`; downstream connection failures and timeouts
return `dependency_unavailable` (502) and `dependency_timeout` (504). Authentication
failures use `authentication_required` (401) and `access_forbidden` (403). Internal
exceptions are never returned to callers.

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

After deployment, the task grants each active Hosted Agent instance identity the `Cognitive Services OpenAI User` role required to invoke the project model.

No `azd` deployment is used.

Run the deployed MVP smoke checks after the job succeeds:

```bash
task app:smoke
```

The smoke runner verifies the web UI form, orchestrator health, all four Hosted Agents, informational and approval-required routing, and an approval transition. It emits machine-readable JSON and returns a nonzero exit code when a check fails.

## Complete deployment shortcut

After the shared infrastructure exists, the following command builds all images, applies the application stack, migrates PostgreSQL, and registers the Hosted Agents:

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
8. The MVP web UI runs exactly one replica; replacing that replica requires clients to refresh the page so antiforgery cookies use the new local Data Protection key.

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
