resource "azurerm_container_app" "litellm" {
  name                         = local.litellm_app_name
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this["litellm"].id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = azurerm_user_assigned_identity.this["litellm"].id
  }

  ingress {
    allow_insecure_connections = false
    external_enabled           = false
    target_port                = 4000
    transport                  = "auto"

    traffic_weight {
      latest_revision = true
      percentage      = 100
    }
  }

  template {
    min_replicas = 1
    max_replicas = 2

    container {
      name   = "litellm"
      image  = local.litellm_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "AZURE_API_BASE"
        value = local.foundry_openai_endpoint
      }

      env {
        name  = "AZURE_API_VERSION"
        value = "2025-04-01-preview"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.this["litellm"].client_id
      }

      env {
        name  = "AZURE_CREDENTIAL"
        value = "ManagedIdentityCredential"
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.cognitive_services_user,
  ]
}
