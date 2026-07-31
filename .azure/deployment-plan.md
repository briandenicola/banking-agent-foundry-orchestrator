# Azure Deployment Plan

> **Status:** Deployed

Generated: 2026-07-31T19:01:09Z

---

## 1. Project Overview

**Goal:** Deploy issue #16's asynchronous workflow API and redesigned Web UI to the existing Azure Container Apps environment while keeping all runtime configuration represented in Terraform.

**Path:** Add Components

---

## 2. Requirements

| Attribute | Value |
|-----------|-------|
| Classification | POC |
| Scale | Small |
| Budget | Cost-Optimized |
| Subscription | BJD_Core_Subscription (`ccfc5dda-43af-4b5e-8cc2-1dda18f2382e`) - confirmed |
| Location | `swedencentral` - confirmed |

---

## 3. Components Detected

| Component | Type | Technology | Path |
|-----------|------|------------|------|
| Orchestrator | API / Worker | .NET 10, Agent Framework | `src/orchestrator/` |
| Web UI | Frontend | ASP.NET Core Razor Pages | `src/webui/` |
| Hosted agents | Agent runtime | Python, LangGraph | `src/agents/python/` |
| LiteLLM | AI gateway | Python container | `src/litellm/` |
| Database migrator | Job | .NET 10 | `src/database-migrator/` |
| Agent deployer | Job | Python | `src/agents/deployer/` |

---

## 4. Recipe Selection

**Selected:** Terraform

**Rationale:** The existing deployment is managed by the `infrastructure/` and `apps/` Terraform stacks. Images use the immutable short commit SHA `8560467`, and the application stack applies that same tag across all Container Apps and jobs.

---

## 5. Architecture

**Stack:** Containers

### Service Mapping

| Component | Azure Service | SKU |
|-----------|---------------|-----|
| Orchestrator | Azure Container Apps | Existing consumption environment |
| Web UI | Azure Container Apps | Existing consumption environment |
| LiteLLM | Azure Container Apps | Existing consumption environment |
| Agent deployer | Azure Container Apps Job | Existing consumption environment |
| Database migrator | Azure Container Apps Job | Existing consumption environment |
| Images | Azure Container Registry | Existing registry |

### Supporting Services

| Service | Purpose |
|---------|---------|
| Application Insights | Traces and application telemetry |
| PostgreSQL | Durable workflow, evidence, approval, and audit state |
| Managed Identity | ACR pull and service authentication |
| Microsoft Foundry | Hosted specialist agents |

---

## 6. Provisioning Limit Checklist

This is an in-place application update. It creates no new Azure resources and consumes no additional resource-count quota.

| Resource Type | Number to Deploy | Total After Deployment | Limit/Quota | Notes |
|---------------|------------------|------------------------|-------------|-------|
| `Microsoft.App/containerApps` | 0 new; 3 updated | 3 existing | N/A for in-place revision updates | Azure CLI inventory; quota CLI returned no Microsoft.App quota records |
| `Microsoft.App/managedEnvironments` | 0 | 2 existing | N/A; unchanged | Azure CLI inventory |
| `Microsoft.ContainerRegistry/registries` | 0 | 1 existing | N/A; unchanged | Existing `hare7040acr` |

**Status:** All resources within limits; no provisioning-count increase.

---

## 7. Execution Checklist

### Phase 1: Planning
- [x] Analyze workspace
- [x] Gather requirements
- [x] Confirm subscription and location with user
- [x] Prepare resource inventory
- [x] Fetch quotas and validate capacity
- [x] Scan codebase
- [x] Select Terraform recipe
- [x] Plan architecture
- [x] User approved this plan

### Phase 2: Execution
- [x] Research existing components
- [x] Infrastructure and Dockerfiles already exist
- [x] Recovery scan, stale threshold, and batch size are represented in `apps/orchestrator.tf`
- [x] Build and push all Terraform-referenced images with tag `8560467`
- [x] Generate and review the Terraform application plan
- [x] Update plan status to `Ready for Validation`

### Phase 3: Validation
- [x] Invoke azure-validate
- [x] Terraform and application checks pass
  - [x] Terraform installation
  - [x] Azure CLI installation and authentication
  - [x] Local-state backend initialized and accessible
  - [x] Terraform format check
  - [x] Terraform syntax validation
  - [x] Terraform plan preview
  - [x] Azure policy inventory
  - [x] Static least-privilege RBAC review
  - [x] Template variable resolution check
  - [x] Complete application quality gate
- [x] Record validation proof
- [x] Set status to `Validated`

### Phase 4: Deployment
- [x] Invoke azure-deploy
- [x] Apply the reviewed Terraform plan
- [x] Verify Terraform has no residual drift
- [x] Verify readiness, async progress, optional evidence, and approvals
- [x] Report deployed endpoints
- [x] Set status to `Deployed`

---

## 7. Validation Proof

| Check | Command Run | Result | Timestamp |
|-------|-------------|--------|-----------|
| Application quality gate | `task test:all` | Pass | 2026-07-31T18:44Z |
| Smoke script tests | `python -m pytest scripts/tests/test_smoke_static.py -q` | 25 passed | 2026-07-31T18:44Z |
| Terraform format | `terraform -chdir=apps fmt -check -recursive` | Pass | 2026-07-31T19:08Z |
| Terraform syntax | `terraform validate` against isolated local-state copy | Pass | 2026-07-31T19:07Z |
| State backend | `terraform state list` | 28 managed objects accessible | 2026-07-31T19:08Z |
| Plan preview | `terraform plan -out=deploy.tfplan -var app_name=hare-7040 -var region=swedencentral -var image_tag=632fa38` | 0 add, 5 update, 0 destroy | 2026-07-31T19:07Z |
| Destructive action check | JSON plan action query | No deletes or replacements | 2026-07-31T19:08Z |
| Azure policy inventory | `az policy assignment list --disable-scope-strict-match` | 14 assignments readable; no plan conflict | 2026-07-31T19:08Z |
| Static RBAC review | Review `apps/roles.tf` principals, roles, and scopes | ACR, Foundry, and Cognitive Services roles are resource-scoped | 2026-07-31T19:08Z |
| Template variables | Search Terraform sources for unresolved `{{ .Env.* }}` | None found | 2026-07-31T19:08Z |
| Deployment apply | `terraform apply postgres-fix.tfplan` | 0 add, 5 in-place updates, 0 destroy | 2026-07-31T19:39Z |
| Live MVP smoke | `python scripts/smoke-mvp.py --timeout 30 --poll-timeout 180` | All checks passed | 2026-07-31T19:47Z |
| Post-deploy drift | `terraform plan -detailed-exitcode ... -var image_tag=8560467` | No changes | 2026-07-31T19:48Z |

**Validated by:** azure-validate skill
**Validation timestamp:** 2026-07-31T19:08:48Z

---

## 8. Files to Generate

| File | Purpose | Status |
|------|---------|--------|
| `.azure/deployment-plan.md` | Deployment source of truth | Complete |
| `apps/orchestrator.tf` | Terraform-managed recovery settings | Committed in `632fa38` |

No new infrastructure or Dockerfiles are required.

---

## 9. Next Steps

> Current: Deployed and verified

1. Monitor the deployed revisions through Application Insights.
2. Configure the GitHub production environment backend/OIDC values before using the gated workflow.
