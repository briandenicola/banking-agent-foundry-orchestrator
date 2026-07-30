locals {
  # Stable UUID for the Workflow.Invoke app role — must not change after first apply.
  workflow_invoke_role_id = "4a1c8e3d-7f2b-4d9e-b6c5-1a3f8d2e0c47"

  # Token scope the Web UI requests; empty when service authentication is disabled.
  orchestrator_token_scope = var.enable_service_auth ? "api://${azuread_application.orchestrator_api[0].client_id}/.default" : ""
}

# App registration for the orchestrator API.
resource "azuread_application" "orchestrator_api" {
  count        = var.enable_service_auth ? 1 : 0
  display_name = "${var.app_name}-orchestrator-api"

  app_role {
    allowed_member_types = ["Application"]
    description          = "Permits callers to submit and approve banking workflow requests."
    display_name         = "Workflow.Invoke"
    enabled              = true
    id                   = local.workflow_invoke_role_id
    value                = "Workflow.Invoke"
  }
}

# Set identifier URI separately to avoid self-referential dependency inside azuread_application.
resource "azuread_application_identifier_uri" "orchestrator_api" {
  count          = var.enable_service_auth ? 1 : 0
  application_id = azuread_application.orchestrator_api[0].id
  identifier_uri = "api://${azuread_application.orchestrator_api[0].client_id}"
}

# Service principal backing the app registration (required for app-role assignments).
resource "azuread_service_principal" "orchestrator_api" {
  count     = var.enable_service_auth ? 1 : 0
  client_id = azuread_application.orchestrator_api[0].client_id
}

# Assign the Workflow.Invoke role to the Web UI managed identity.
resource "azuread_app_role_assignment" "webui_workflow_invoke" {
  count               = var.enable_service_auth ? 1 : 0
  app_role_id         = local.workflow_invoke_role_id
  principal_object_id = azurerm_user_assigned_identity.this["webui"].principal_id
  resource_object_id  = azuread_service_principal.orchestrator_api[0].object_id
}
