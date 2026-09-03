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
  webui_auth_enabled = var.webui_auth_client_id != ""

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

resource "azapi_resource" "webui_auth" {
  count = local.webui_auth_enabled ? 1 : 0

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
            openIdIssuer            = "https://login.microsoftonline.com/${data.azurerm_client_config.current.tenant_id}/v2.0"
            clientId                = var.webui_auth_client_id
            clientSecretSettingName = local.webui_auth_secret_name
          }
          validation = {
            allowedAudiences = [var.webui_auth_client_id]
          }
        }
      }

      login = {
        # The token store is not used to call downstream services -- see the
        # note at the top of this file -- but Easy Auth needs it to keep the
        # session cookie working across the single replica.
        tokenStore = {
          enabled = true
        }
      }
    }
  }

  depends_on = [azurerm_container_app.webui]
}
