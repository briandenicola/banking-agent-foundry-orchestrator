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

data "azurerm_application_insights" "this" {
  name                = "${var.app_name}-appinsights"
  resource_group_name = local.core_rg_name
}
