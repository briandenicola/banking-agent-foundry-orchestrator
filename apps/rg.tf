resource "azurerm_resource_group" "this" {
  name     = local.apps_rg_name
  location = var.region
  tags     = local.tags
}
