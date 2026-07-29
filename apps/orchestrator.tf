resource "azurerm_container_app" "orchestrator" {
  name                         = local.orchestrator_app_name
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this["orchestrator"].id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = azurerm_user_assigned_identity.this["orchestrator"].id
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
    max_replicas = 3

    container {
      name   = "orchestrator"
      image  = local.orchestrator_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.this["orchestrator"].client_id
      }

      env {
        name  = "FOUNDRY_AGENT_ENDPOINT"
        value = local.foundry_project_endpoint
      }

      env {
        name  = "FOUNDRY_AGENT_NAME"
        value = "${var.app_name}-agent"
      }

      env {
        name  = "FOUNDRY_SCOPE"
        value = "https://ai.azure.com/.default"
      }

      env {
        name  = "FOUNDRY_TOOL_ENDPOINTS"
        value = local.foundry_tool_endpoints
      }

      env {
        name  = "POSTGRESQL_HOST"
        value = local.postgresql_host
      }

      env {
        name  = "POSTGRESQL_DATABASE"
        value = local.postgresql_database
      }

      env {
        name  = "POSTGRESQL_USER"
        value = azurerm_user_assigned_identity.this["orchestrator"].name
      }

      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = data.azurerm_application_insights.this.connection_string
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.orchestrator_agent_consumer,
  ]
}
