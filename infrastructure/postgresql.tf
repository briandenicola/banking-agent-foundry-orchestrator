resource "azurerm_postgresql_flexible_server" "this" {
  name                          = local.postgresql_server_name
  resource_group_name           = azurerm_resource_group.this.name
  location                      = azurerm_resource_group.this.location
  version                       = "16"
  storage_mb                    = 32768
  sku_name                      = "B_Standard_B1ms"
  backup_retention_days         = 7
  delegated_subnet_id           = var.enable_private_networking ? azurerm_subnet.postgresql[0].id : null
  private_dns_zone_id           = var.enable_private_networking ? azurerm_private_dns_zone.postgresql[0].id : null
  public_network_access_enabled = var.enable_private_networking ? false : true
  tags                          = local.tags

  identity {
    type = "SystemAssigned"
  }

  authentication {
    active_directory_auth_enabled = true
    password_auth_enabled         = false
    tenant_id                     = data.azurerm_client_config.current.tenant_id
  }

  lifecycle {
    ignore_changes = [
      zone,
      identity[0].identity_ids,
    ]
  }

  depends_on = [
    azurerm_private_dns_zone_virtual_network_link.postgresql,
  ]
}

resource "azurerm_user_assigned_identity" "database_migrator" {
  name                = "${local.resource_name}-database-migrator-identity"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.tags
}

resource "azurerm_postgresql_flexible_server_active_directory_administrator" "current" {
  server_name         = azurerm_postgresql_flexible_server.this.name
  resource_group_name = azurerm_resource_group.this.name
  tenant_id           = data.azurerm_client_config.current.tenant_id
  object_id           = azurerm_user_assigned_identity.database_migrator.principal_id
  principal_name      = azurerm_user_assigned_identity.database_migrator.name
  principal_type      = "ServicePrincipal"

  lifecycle {
    create_before_destroy = true
  }
}

resource "azurerm_postgresql_flexible_server_database" "this" {
  name      = local.postgresql_database_name
  server_id = azurerm_postgresql_flexible_server.this.id
  charset   = "UTF8"
  collation = "en_US.utf8"
}

resource "azurerm_postgresql_flexible_server_firewall_rule" "azure_services" {
  count = var.enable_private_networking ? 0 : 1

  # Demo-grade compatibility path: 0.0.0.0 means "Allow Azure services",
  # which admits resources from any Azure tenant at the network layer.
  # Set enable_private_networking=true for shared or regulated environments.
  name             = "AllowAzureServices"
  server_id        = azurerm_postgresql_flexible_server.this.id
  start_ip_address = "0.0.0.0"
  end_ip_address   = "0.0.0.0"
}