resource "azurerm_container_registry" "this" {
  name                          = local.acr_account_name
  resource_group_name           = azurerm_resource_group.this.name
  location                      = azurerm_resource_group.this.location
  sku                           = "Standard"
  admin_enabled                 = false
  anonymous_pull_enabled        = false
  public_network_access_enabled = true
  tags                          = local.tags
}
