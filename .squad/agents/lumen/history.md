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

### 2026-07-30 — Workstream 2 Phase A: Entra service-to-service auth

**What was implemented (non-colliding, Theo-independent):**

**Terraform (`apps/`):**

| File | Change |
|------|--------|
| `providers.tf` | Added `hashicorp/azuread ~> 3.0` provider; installed as v3.9.0 |
| `references.tf` | Added `data "azurerm_client_config" "current" {}` for tenant_id |
| `entra.tf` | New file: `azuread_application` (Workflow.Invoke app role), `azuread_application_identifier_uri` (api://{client_id}), `azuread_service_principal`, `azuread_app_role_assignment` for webui UAI |
| `webui.tf` | Added `AZURE_CLIENT_ID` (webui identity client_id) + `ORCHESTRATOR_TOKEN_SCOPE` env vars; added `azuread_app_role_assignment.webui_workflow_invoke` to `depends_on` |
| `orchestrator.tf` | Added `AZURE_TENANT_ID` (from client config) + `ORCHESTRATOR_APP_ID` (Entra app client_id) env vars |
| `outputs.tf` | Added `ORCHESTRATOR_TOKEN_SCOPE` output for smoke tests |

**Web UI (`src/webui/`):**

| File | Change |
|------|--------|
| `webui.csproj` | Added `Azure.Identity 1.21.0` (matches database-migrator/infrastructure versions) |
| `OrchestratorTokenHandler.cs` | New global-namespace `DelegatingHandler`; calls `credential.GetTokenAsync` per request; attaches `Authorization: Bearer` |
| `Program.cs` | Uses `ManagedIdentityCredential(ManagedIdentityId.FromUserAssignedClientId(...))` when `AZURE_CLIENT_ID` set (production); falls back to `DefaultAzureCredential` for dev. Registers handler conditionally when `ORCHESTRATOR_TOKEN_SCOPE` is set. |

**Validation:**
- `terraform fmt -recursive` → no changes
- `terraform init -backend=false` → `azuread v3.9.0` installed, lock file updated
- `terraform validate` → `Success! The configuration is valid.`
- `dotnet build -c Release` → 0 warnings, 0 errors

**Key Terraform patterns learned:**
- `azuread_application_identifier_uri` as a separate resource avoids the self-reference problem when `identifier_uris = ["api://{client_id}"]` — `client_id` is a computed attribute and can't appear in the same `azuread_application` block.
- `azuread_application` + `azuread_service_principal` must both be explicitly created; Terraform's `azuread_application` does NOT auto-create the service principal.
- App role `id` must be a stable hardcoded UUID — do not use `random_uuid` just to avoid adding a provider for one value.
- `azuread ~> 3.0` (v3.9.0 resolved) installs cleanly alongside `azurerm ~> 4.0` and `azapi ~> 2.0`.

**Orchestrator JWT integration (NOT implemented — Theo goes first):**

See `decisions/inbox/lumen-service-auth.md` for the exact follow-up spec.

**Env vars / config names the orchestrator Program.cs will consume:**
- `AZURE_TENANT_ID` — Entra tenant ID (injected by Terraform)
- `ORCHESTRATOR_APP_ID` — the Entra app's client_id (injected by Terraform, used as JWT audience)

**Required orchestrator changes (after Theo):**
1. `orchestrator.csproj`: add `Microsoft.Identity.Web` (e.g. `1.25.*`)
2. `Program.cs` service registration:
   ```csharp
   builder.Services
       .AddAuthentication(JwtBearerDefaults.AuthenticationScheme)
       .AddMicrosoftIdentityWebApi(builder.Configuration, jwtBearerScheme: JwtBearerDefaults.AuthenticationScheme);
   ```
   Configure via `AzureAd__TenantId` = `AZURE_TENANT_ID` and `AzureAd__ClientId` = `ORCHESTRATOR_APP_ID`.
3. `Program.cs` middleware: add `app.UseAuthentication();` before `app.UseAuthorization();`
4. `WorkflowEndpoints.cs`: add `.RequireAuthorization()` to the workflow endpoint group; health check stays open.

### 2026-07-31 — Issue #9 Hosted-agent test coverage

**What was implemented:**

**`app/hosted.py` (production-safe refactor):**
- Added `asyncio.wait_for` around `graph.ainvoke` with a configurable deadline (`AGENT_INVOKE_TIMEOUT_SECONDS` env var, default 30s).
- `asyncio.TimeoutError` → 504 JSON response with `{"error": "timeout", "detail": "...Xs"}`.
- `pydantic.ValidationError` from malformed request body → 400 JSON response with `{"error": "invalid_request", "detail": [...errors...]}`.
- Unhandled `Exception` from graph → 500 JSON response with `{"error": "agent_error", "detail": str(exc)}` — no traceback leakage.
- Added `logging.getLogger(__name__)` for structured error context.

**`tests/test_hosted.py` (13 new tests):**
All drive the real `InvocationAgentServerHost` ASGI app via `httpx.AsyncClient + ASGITransport`. Module-level `graph` is replaced with `AsyncMock` per test via `patch.object(hosted_module, "graph", ...)`.

| Scenario | Test name | Status code |
|---|---|---|
| Valid success, full AgentResult schema | `test_valid_request_returns_200_with_agent_result_schema` | 200 |
| Graph RuntimeError | `test_graph_runtime_error_returns_500` | 500 |
| Graph ValueError | `test_graph_value_error_returns_500_with_detail` | 500 |
| Hung graph (timeout) | `test_timeout_returns_504` | 504 |
| Timeout yields no partial result | `test_short_timeout_does_not_return_partial_state` | 504 |
| Missing `message` field | `test_missing_message_field_returns_400` | 400 |
| Empty `message` string (min_length=1) | `test_empty_message_returns_400` | 400 |
| Non-object JSON payload | `test_non_object_payload_returns_400` | 400 |
| Content-type is JSON | `test_response_content_type_is_json` | 200 |
| trace_id propagated | `test_response_trace_id_propagated_from_request` | 200 |
| No traceback in 500 detail | `test_error_response_body_never_leaks_stack_trace` | 500 |
| requires_approval always present | `test_success_response_requires_approval_field_present` | 200 |
| risk_level is valid enum | `test_success_response_risk_level_is_valid_enum` | 200 |

**Test run:** 13/13 passed in 1.48s. Existing 4 `test_agents.py` tests unchanged and still pass.

**Key learnings:**
- `InvocationAgentServerHost` is a Starlette ASGI app; `httpx.ASGITransport` is the cleanest in-process test driver.
- Module-level state in `hosted.py` (`graph`, `agent_name`, `app`) is fine to patch with `patch.object(hosted_module, "graph", mock)` — the `@app.invoke_handler` closure captures the name at call time, not at decoration time, so swapping the module attribute works.
- `azure-ai-agentserver` package emits OTEL span/metric JSON to stderr during tests — benign but noisy; filtered via grep in CI if needed.
- `asyncio.wait_for` timeout was 0.05s in tests to keep the suite fast; `_INVOKE_TIMEOUT` patched via `patch.object`.
- No conftest.py was needed — all setup inline per test class.
