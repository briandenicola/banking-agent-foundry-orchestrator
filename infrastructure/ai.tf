resource "azapi_resource" "foundry" {
  type                      = "Microsoft.CognitiveServices/accounts@2025-10-01-preview"
  name                      = local.foundry_name
  parent_id                 = azurerm_resource_group.this.id
  location                  = azurerm_resource_group.this.location
  schema_validation_enabled = false
  tags                      = local.tags

  body = {
    kind = "AIServices"
    sku = {
      name = "S0"
    }
    identity = {
      type = "SystemAssigned"
    }
    properties = {
      disableLocalAuth       = true
      allowProjectManagement = true
      customSubDomainName    = local.foundry_name
      publicNetworkAccess    = "Enabled"
    }
  }

  response_export_values = [
    "identity.principalId",
    "properties.endpoint"
  ]
}

resource "azapi_resource" "foundry_project" {
  type                      = "Microsoft.CognitiveServices/accounts/projects@2025-10-01-preview"
  name                      = local.foundry_project_name
  parent_id                 = azapi_resource.foundry.id
  location                  = azurerm_resource_group.this.location
  schema_validation_enabled = false

  body = {
    sku = {
      name = "S0"
    }
    identity = {
      type = "SystemAssigned"
    }
    properties = {
      displayName = local.foundry_project_name
      description = "Banking agent hosted agents"
    }
  }
}

resource "azapi_resource" "gpt54_mini" {
  type      = "Microsoft.CognitiveServices/accounts/deployments@2025-10-01-preview"
  name      = local.model_deployment_name
  parent_id = azapi_resource.foundry.id

  body = {
    sku = {
      name     = "GlobalStandard"
      capacity = 10
    }
    properties = {
      model = {
        format  = "OpenAI"
        name    = local.model_deployment_name
        version = local.model_version
      }
    }
  }
}

data "azurerm_monitor_diagnostic_categories" "foundry" {
  resource_id = azapi_resource.foundry.id
}

resource "azurerm_monitor_diagnostic_setting" "foundry" {
  name                       = "${local.resource_name}-foundry-diag"
  target_resource_id         = azapi_resource.foundry.id
  log_analytics_workspace_id = azurerm_log_analytics_workspace.this.id

  dynamic "enabled_log" {
    for_each = toset(data.azurerm_monitor_diagnostic_categories.foundry.log_category_types)
    content {
      category = enabled_log.value
    }
  }

  enabled_metric {
    category = "AllMetrics"
  }
}
