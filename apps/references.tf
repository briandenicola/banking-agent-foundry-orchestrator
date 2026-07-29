data "azurerm_container_registry" "this" {
  name                = local.acr_name
  resource_group_name = local.core_rg_name
}

data "azurerm_container_app_environment" "this" {
  name                = local.cae_name
  resource_group_name = local.core_rg_name
}

data "azurerm_cognitive_account" "foundry" {
  name                = local.foundry_name
  resource_group_name = local.core_rg_name
}

data "azapi_resource" "foundry_project" {
  type      = "Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview"
  name      = local.foundry_project
  parent_id = data.azurerm_cognitive_account.foundry.id
}

data "azurerm_application_insights" "this" {
  name                = "${var.app_name}-appinsights"
  resource_group_name = local.core_rg_name
}
