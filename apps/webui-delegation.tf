# Delegated user authentication for the Web UI.
#
# When this is on the Web UI runs its own OpenID Connect sign-in and acquires an
# orchestrator token for the signed-in user, so the orchestrator can verify which
# customer a request is for rather than trust an asserted identifier.
#
# It replaces, rather than supplements, Container Apps built-in authentication:
# both would sign the same user in, and the platform's redirect would intercept
# the application's own callback. `apps/webui-auth.tf` is therefore absent while
# this is on.
#
# See docs/decisions/0005-delegated-user-authentication.md for why this is not
# on-behalf-of. In short, OBO needs the Easy Auth token store, the token store is
# backed only by a blob SAS, and subscription policy here forbids shared-key
# storage access — and an application that runs its own sign-in holds its own
# refresh token and so never needs the exchange.

locals {
  # Delegation reuses the sign-in registration, so it depends on the same inputs
  # Easy Auth does: without a client ID and secret there is nobody to sign in
  # against, and without an orchestrator app ID there is no audience to request.
  user_delegation_enabled = var.enable_user_delegation && local.webui_auth_enabled && var.orchestrator_api_app_id != ""

  orchestrator_api_scope = local.user_delegation_enabled ? "api://${var.orchestrator_api_app_id}/user_impersonation" : ""
}
