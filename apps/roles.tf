resource "azurerm_role_assignment" "acr_pull" {
  for_each = azurerm_user_assigned_identity.this

  scope                            = data.azurerm_container_registry.this.id
  role_definition_name             = "AcrPull"
  principal_id                     = each.value.principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "cognitive_services_user" {
  for_each = toset(["orchestrator", "litellm"])

  scope                            = data.azurerm_cognitive_account.foundry.id
  role_definition_name             = "Cognitive Services User"
  principal_id                     = azurerm_user_assigned_identity.this[each.key].principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "orchestrator_agent_consumer" {
  scope                            = data.azapi_resource.foundry_project.id
  role_definition_name             = "Foundry Agent Consumer"
  principal_id                     = azurerm_user_assigned_identity.this["orchestrator"].principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "agent_deployer_project_manager" {
  scope                            = data.azapi_resource.foundry_project.id
  role_definition_name             = "Foundry Project Manager"
  principal_id                     = azurerm_user_assigned_identity.this["agent-deployer"].principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "foundry_project_acr_pull" {
  scope                            = data.azurerm_container_registry.this.id
  role_definition_name             = "AcrPull"
  principal_id                     = data.azapi_resource.foundry_project.identity[0].principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "foundry_project_user" {
  scope                            = data.azurerm_cognitive_account.foundry.id
  role_definition_name             = "Foundry User"
  principal_id                     = data.azapi_resource.foundry_project.identity[0].principal_id
  skip_service_principal_aad_check = true
}
