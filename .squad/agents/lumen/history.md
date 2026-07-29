# Lumen History

Project: langgraph-learnings
Stack: C#, .NET 8, MCP, Microsoft Foundry, LangGraph, LiteLLM, Azure Container Apps
Owner: brian
Description: A banking agent prototype where a C# orchestrator uses Microsoft Agent Framework and MCP to invoke Foundry-hosted LangGraph agents.

## Learnings

### 2026-07-29 — Full Terraform stack under `infra/`

**File layout (flat root module, `region` is the only variable):**

| File | Contents |
| --- | --- |
| `providers.tf` | `required_version >= 1.6.0`; azurerm `~>4`, azapi `~>2`, random `~>3`, time `~>0.11`; `data.azurerm_client_config.current` |
| `variables.tf` | `region` (default `eastus`) — the only input |
| `locals.tf` | all naming/endpoints/images; one shared `random_string.suffix` (len 5, lower+numeric) |
| `main.tf` | resource group, Log Analytics, Application Insights (workspace-based), Container Apps Environment (Consumption, no VNet) |
| `registry.tf` | ACR Standard, `admin_enabled=false`, `anonymous_pull_enabled=false` |
| `identity.tf` | 4 UAIs via `for_each`; AcrPull ×4, Cognitive Services User ×2, Azure AI Developer ×1, Key Vault Secrets User ×1 |
| `ai.tf` | azapi Foundry account + project + model deployment; diagnostic setting to LAW |
| `keyvault.tf` | RBAC vault, deployer Secrets Officer, `time_sleep` for RBAC propagation |
| `horizondb.tf` | `random_password`, azapi cluster with region precondition, 3 KV secrets |
| `litellm.tf` | LiteLLM container app, internal ingress :4000 |
| `container_apps.tf` | orchestrator / webui / agents |
| `outputs.tf` | non-secret outputs + first-apply sequencing note |

**Naming convention:** `project = banking-agent`, `environment = dev`,
`name_prefix = bankingagentdev`, `resource_group_name = rg-banking-agent-dev`.
One `random_string.suffix` feeds every globally-unique name: ACR
`bankingagentdevacr<sfx>` (alnum only), Key Vault `kv-bankingagent-<sfx>`
(<=24), Foundry `bankingagentdev-foundry-<sfx>`, HorizonDB
`bankingagentdev-hdb-<sfx>`.

**Exact resource types used:**
- `Microsoft.CognitiveServices/accounts@2025-10-01-preview` (kind `AIServices`,
  `disableLocalAuth: true`, `allowProjectManagement: true`)
- `Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview`
- `Microsoft.CognitiveServices/accounts/deployments@2025-06-01`
  (`gpt-4o-mini` / `OpenAI` / `2024-07-18`, sku `GlobalStandard` capacity 10)
- `Microsoft.HorizonDB/clusters@2026-01-20-preview`

**azapi / Terraform gotchas hit:**
1. `sensitive_body` on `azapi_resource` is a **write-only attribute** and needs
   Terraform >= 1.11. On 1.7.5 it errors with *"WriteOnly Attribute Not
   Allowed"*. Fell back to putting the password in `body`; `random_password`
   sensitivity still redacts the plan.
2. HorizonDB `version` is typed **string** in the property table even though the
   Bicep sample uses an int literal (`param version int = 17`). `"17"` works.
   `createMode` valid values are `Create` / `PointInTimeRestore` / `Update`.
3. HorizonDB `network` is documented as an object with **zero properties** —
   opaque. Omit it and set `schema_validation_enabled = false`.
4. The preview API documents **no read-only FQDN property**. Used
   `try(azapi_resource.horizondb.output.properties.fullyQualifiedDomainName,
   "<cluster>.postgres.database.azure.com")` with `response_export_values = ["properties"]`.
   `az resource show --query properties.fullyQualifiedDomainName` is the way to
   confirm post-apply.
5. `azurerm_key_vault.enable_rbac_authorization` is **deprecated** in azurerm
   4.81 — use `rbac_authorization_enabled`. Shows up as a `validate` warning.
6. `data.azurerm_monitor_diagnostic_categories` on an azapi resource id defers
   its read to apply time ("config refers to values not yet known"). Works fine;
   no `depends_on` needed.
7. Key Vault with `rbac_authorization_enabled` means the *deployer* also needs
   `Key Vault Secrets Officer` before `azurerm_key_vault_secret` will succeed,
   plus a `time_sleep` (~30s) for RBAC propagation.

**Container Apps patterns that matter:**
- One UAI per app, created first so RBAC lands before the app. System-assigned
  identities cannot do this — the initial ACR pull and Key Vault reference both
  need the grant to pre-exist.
- `registry { server, identity = uai.id }` + `AZURE_CLIENT_ID = uai.client_id`.
  Without `AZURE_CLIENT_ID`, `DefaultAzureCredential` cannot pick between
  identities.
- Key Vault reference: `secret { name, key_vault_secret_id = <secret>.versionless_id,
  identity = uai.id }` then `env { name, secret_name }`. Use the **versionless**
  id so rotation does not require a redeploy.
- Internal ingress FQDN is available as `azurerm_container_app.<x>.ingress[0].fqdn`
  and resolves as `<app>.internal.<env-domain>`.
- Dependency chain is acyclic: webui -> orchestrator -> agents -> litellm.

**Validation loop:** `terraform -chdir=infra fmt -recursive`, then
`init -backend=false`, then `validate`. A real `plan -refresh=false -var region=westus3`
also succeeded in the sandbox (Azure creds were present) and is a much stronger
check — it caught nothing extra, but it did confirm the precondition fires
correctly on `eastus` and that the password is redacted in plan output.

**Open runtime questions:**
- LiteLLM `azure_ad_token_provider: "default"` in YAML is unverified; set
  `AZURE_CREDENTIAL=ManagedIdentityCredential` alongside it as a hedge.
- The upstream `ghcr.io/berriai/litellm:main-stable` image runs as root and has
  no named non-root user; forced `USER 1000` with `HOME=/tmp` for cache writes.
  Needs a smoke test.
