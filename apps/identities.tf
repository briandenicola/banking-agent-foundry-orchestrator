locals {
  app_identities = toset(["orchestrator", "webui", "agent-deployer"])
}

resource "azurerm_user_assigned_identity" "this" {
  for_each = local.app_identities

  name                = "${var.app_name}-${each.key}-identity"
  resource_group_name = azurerm_resource_group.this.name
  location            = azurerm_resource_group.this.location
  tags                = local.tags
}
