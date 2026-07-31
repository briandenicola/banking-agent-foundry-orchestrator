# Terraform Remote State

## Overview

Both Terraform stacks (`infrastructure/` and `apps/`) store state in Azure Blob Storage, using OIDC plus Microsoft Entra data-plane authentication and environment-prefixed state keys. Shared-key access is disabled.

Azure Blob automatically provides state locking via blob leases. No additional lock table is required.

## Storage layout

```
<container>/                         # e.g. tfstate
  <environment>/                     # e.g. production
    infrastructure.tfstate
    apps.tfstate
```

## Backend configuration (partial config pattern)

Neither `infrastructure/backend.tf` nor `apps/backend.tf` contains connection details. All values are injected at `terraform init` time via `-backend-config` flags. This means no credentials are stored in source control.

Example (local developer):

```sh
terraform -chdir=infrastructure init \
  -backend-config="resource_group_name=tfstate-rg" \
  -backend-config="storage_account_name=myaccount" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=production/infrastructure.tfstate"
```

CI uses `terraform init -backend=false` so validation requires no credentials.

## One-time bootstrap

Run `scripts/bootstrap-remote-state.sh` once per subscription before the first real deployment.

```sh
./scripts/bootstrap-remote-state.sh \
  --subscription  <subscription-id>  \
  --resource-group tfstate-rg        \
  --storage-account <globally-unique-name> \
  --container tfstate                \
  --location swedencentral
```

The script creates the resource group, storage account (LRS, HTTPS-only, soft-delete 30 days, versioning), and blob container. It also prints the exact values needed for GitHub.

## Per-environment separation

Each deployment environment uses a distinct key prefix. Region remains an independent Terraform input rather than an environment identifier.

| Environment | Infrastructure state key | Apps state key |
|-------------|--------------------------|----------------|
| production | `production/infrastructure.tfstate` | `production/apps.tfstate` |
| staging | `staging/infrastructure.tfstate` | `staging/apps.tfstate` |

Use a separate GitHub Environment, approval policy, and backend key prefix for each environment. Never point two environments at the same key.

Existing local state must be migrated deliberately before enabling the production workflow:

```sh
terraform -chdir=infrastructure init -migrate-state \
  -backend-config="resource_group_name=<state-rg>" \
  -backend-config="storage_account_name=<state-account>" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=production/infrastructure.tfstate"

terraform -chdir=apps init -migrate-state \
  -backend-config="resource_group_name=<state-rg>" \
  -backend-config="storage_account_name=<state-account>" \
  -backend-config="container_name=tfstate" \
  -backend-config="key=production/apps.tfstate"
```

## Required GitHub configuration

### Repository variables (Settings → Variables)

| Variable | Description |
|----------|-------------|
| `AZURE_CLIENT_ID` | App (client) ID of the deployment service principal |
| `AZURE_TENANT_ID` | Entra tenant ID |
| `AZURE_SUBSCRIPTION_ID` | Target Azure subscription |
| `AZURE_REGION` | Default deployment region (e.g. `swedencentral`) |

### Environment secrets (Settings → Environments → production)

| Secret | Description |
|--------|-------------|
| `TF_BACKEND_RESOURCE_GROUP` | Resource group containing the state storage account |
| `TF_BACKEND_STORAGE_ACCOUNT` | Storage account name |
| `TF_BACKEND_CONTAINER` | Blob container name (e.g. `tfstate`) |

## OIDC federated credential setup

The deployment service principal requires a federated credential for each GitHub OIDC subject that must authenticate:

```
repo:<org>/<repo>:environment:production   # deploy jobs
```

The SP also requires:
- `Contributor` on the target subscription (or narrower scopes per resource group)
- `Role Based Access Control Administrator` where Terraform creates role assignments
- `Storage Blob Data Contributor` on the Terraform state storage account
- Appropriate Microsoft Graph application permissions if `enable_service_auth` is enabled

Create the Entra application and service principal without a password or client secret, then add a federated credential with `az ad app federated-credential create` or the Entra portal. The credential must use:

- issuer: `https://token.actions.githubusercontent.com`
- subject: `repo:<org>/<repo>:environment:production`
- audience: `api://AzureADTokenExchange`

Never use `az ad sp create-for-rbac --sdk-auth`, create a client secret, or store an Azure credential JSON document in GitHub. OIDC token exchange is the only supported authentication method.

The GitHub `production` environment must define required reviewers. The deployment workflow is triggered only after the `CI` workflow succeeds for a push to `main`, or by an explicitly approved manual dispatch.

If orchestrator service authentication is enabled, grant the deployment identity the `Workflow.Invoke` application role so the post-deployment smoke test can obtain a valid orchestrator token.

## Locking behaviour

Azure Blob Storage uses blob leases for locking:
- A lock is acquired automatically during `terraform apply` / `terraform plan -lock=true`.
- If a run is interrupted, inspect the lease and active GitHub run before taking action.
- Forcibly releasing a stale lock: `terraform force-unlock <lock-id>` — use only when the holder confirms it is no longer running.
