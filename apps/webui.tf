resource "azurerm_container_app" "webui" {
  name                         = local.webui_app_name
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this["webui"].id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = azurerm_user_assigned_identity.this["webui"].id
  }

  ingress {
    allow_insecure_connections = false
    external_enabled           = true
    target_port                = 8080
    transport                  = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 1

    container {
      name   = "webui"
      image  = local.webui_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }

      env {
        name  = "ORCHESTRATOR_API_BASE_URL"
        value = "https://${azurerm_container_app.orchestrator.ingress[0].fqdn}"
      }

      env {
        name  = "DATA_PROTECTION_KEYS_PATH"
        value = "/tmp/banking-agent-data-protection"
      }

      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = data.azurerm_application_insights.this.connection_string
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
  ]
}
