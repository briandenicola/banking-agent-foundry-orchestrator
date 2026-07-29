resource "azurerm_role_assignment" "acr_pull" {
  for_each = azurerm_user_assigned_identity.this

  scope                            = data.azurerm_container_registry.this.id
  role_definition_name             = "AcrPull"
  principal_id                     = each.value.principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "cognitive_services_user" {
  for_each = toset(["orchestrator", "agents", "litellm"])

  scope                            = data.azurerm_cognitive_account.foundry.id
  role_definition_name             = "Cognitive Services User"
  principal_id                     = azurerm_user_assigned_identity.this[each.key].principal_id
  skip_service_principal_aad_check = true
}

resource "azurerm_role_assignment" "ai_developer" {
  scope                            = data.azurerm_cognitive_account.foundry.id
  role_definition_name             = "Azure AI Developer"
  principal_id                     = azurerm_user_assigned_identity.this["orchestrator"].principal_id
  skip_service_principal_aad_check = true
}
