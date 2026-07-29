resource "azurerm_storage_account" "webui_data_protection" {
  name                            = "${replace(var.app_name, "-", "")}dp"
  resource_group_name             = azurerm_resource_group.this.name
  location                        = azurerm_resource_group.this.location
  account_tier                    = "Standard"
  account_replication_type        = "LRS"
  min_tls_version                 = "TLS1_2"
  shared_access_key_enabled       = false
  public_network_access_enabled   = true
  allow_nested_items_to_be_public = false
  tags                            = local.tags
}

resource "azurerm_storage_container" "webui_data_protection" {
  name                  = "data-protection"
  storage_account_id    = azurerm_storage_account.webui_data_protection.id
  container_access_type = "private"
}

resource "azurerm_role_assignment" "webui_data_protection" {
  scope                            = azurerm_storage_account.webui_data_protection.id
  role_definition_name             = "Storage Blob Data Contributor"
  principal_id                     = azurerm_user_assigned_identity.this["webui"].principal_id
  skip_service_principal_aad_check = true
}
