# Container Apps built-in authentication ("Easy Auth") for the Web UI.
#
# Why this is bring-your-own rather than a managed app registration
# -----------------------------------------------------------------
# The target tenant denies the app registration Terraform would need to create,
# which is the same constraint that forces `enable_service_auth = false` (see
# entra.tf and issue #40). So the registration is created by hand and its client
# ID and secret are supplied as inputs. When `webui_auth_client_id` is empty the
# whole feature is absent and the Web UI stays public, which is the behaviour
# every existing deployment already has.
#
# What this does and does not buy
# -------------------------------
# It authenticates the *person* using the Web UI, which is what issue #40 asks
# for. It does not let the application call Foundry as that person: workflows
# execute in the background through the recovery worker, long after the sign-in
# token has gone, so carrying a user token into a workflow would mean persisting
# user credentials in the workflow store. The signed-in identity is instead used
# as a memory scope the orchestrator asserts on the user's behalf.

locals {
  # The registration is usable whenever it is supplied. Whether Easy Auth or the
  # application's own OpenID Connect handler consumes it is decided below.
  webui_auth_enabled = var.webui_auth_client_id != ""

  # The tenant that signs users in is independent of the tenant the Azure
  # resources live in. Defaulting to the deployment's own tenant preserves the
  # single-tenant setup; overriding it lets the registration and its users live
  # in a tenant the operator controls, which is the only way to get real
  # multi-user sign-in when the subscription's tenant denies app registration.
  # Nothing else moves: the managed identities, Foundry, and every data-plane
  # call stay in the deployment tenant.
  webui_auth_tenant_id = coalesce(
    var.webui_auth_tenant_id,
    data.azurerm_client_config.current.tenant_id,
  )

  # Container App secret names must be lowercase alphanumeric or dashes.
  webui_auth_secret_name = "webui-auth-client-secret"

  # Easy Auth intercepts every request, including the platform's own probe
  # traffic. Without these exclusions the probes receive a login redirect, the
  # revision never becomes ready, and the deployment fails in a way that looks
  # nothing like an authentication problem.
  webui_auth_excluded_paths = [
    "/health/live",
    "/health/ready",
  ]
}

# Easy Auth and in-application sign-in are alternatives, not layers. With both
# on, the platform's redirect intercepts the application's own /signin-oidc
# callback and sign-in can never complete, so this resource is absent whenever
# delegation is on.
resource "azapi_resource" "webui_auth" {
  count = local.webui_auth_enabled && !local.user_delegation_enabled ? 1 : 0

  # The auth configuration is a singleton child resource and must be named
  # "current"; the API rejects any other name.
  type      = "Microsoft.App/containerApps/authConfigs@2024-03-01"
  name      = "current"
  parent_id = azurerm_container_app.webui.id

  body = {
    properties = {
      platform = {
        enabled = true
      }

      globalValidation = {
        unauthenticatedClientAction = "RedirectToLoginPage"
        redirectToProvider          = "azureactivedirectory"
        excludedPaths               = local.webui_auth_excluded_paths
      }

      identityProviders = {
        azureActiveDirectory = {
          enabled = true
          registration = {
            openIdIssuer            = "https://login.microsoftonline.com/${local.webui_auth_tenant_id}/v2.0"
            clientId                = var.webui_auth_client_id
            clientSecretSettingName = local.webui_auth_secret_name
          }
          validation = {
            allowedAudiences = [var.webui_auth_client_id]
          }

        }
      }

      login = {
        # Off, deliberately. Enabling it on Container Apps requires a storage
        # account and a blob SAS URL setting, and the API rejects the config
        # outright without one. That is a real cost for no benefit here:
        # EasyAuthCustomerAccessor reads only the X-MS-CLIENT-PRINCIPAL headers,
        # which are injected on every authenticated request whether or not the
        # store exists. Sign-in and the session cookie are unaffected.
        #
        # It is also unusable in some subscriptions regardless of what we want:
        # a policy that sets allowSharedKeyAccess = false makes the SAS return
        # 403 on first use while sign-in continues to look healthy. That is what
        # ruled out on-behalf-of; see
        # docs/decisions/0005-delegated-user-authentication.md.
        tokenStore = {
          enabled = false
        }
      }
    }
  }

  depends_on = [azurerm_container_app.webui]
}
