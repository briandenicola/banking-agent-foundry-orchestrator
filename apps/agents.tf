resource "azurerm_container_app" "agents" {
  name                         = local.agents_app_name
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this["agents"].id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = azurerm_user_assigned_identity.this["agents"].id
  }

  ingress {
    allow_insecure_connections = false
    external_enabled           = false
    target_port                = 8000
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
      name   = "agents"
      image  = local.agents_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "PORT"
        value = "8000"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.this["agents"].client_id
      }

      env {
        name  = "LITELLM_BASE_URL"
        value = local.litellm_internal_url
      }

      env {
        name  = "LITELLM_MODEL"
        value = local.model_deployment
      }

      env {
        name  = "APPLICATIONINSIGHTS_CONNECTION_STRING"
        value = data.azurerm_application_insights.this.connection_string
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.cognitive_services_user,
  ]
}
