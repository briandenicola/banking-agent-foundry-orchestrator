resource "azurerm_container_app_job" "database_migrator" {
  name                         = "${var.app_name}-database-migrator"
  resource_group_name          = azurerm_resource_group.this.name
  location                     = azurerm_resource_group.this.location
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  replica_timeout_in_seconds   = 900
  replica_retry_limit          = 1
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [data.azurerm_user_assigned_identity.database_migrator.id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = data.azurerm_user_assigned_identity.database_migrator.id
  }

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  template {
    container {
      name   = "database-migrator"
      image  = local.database_migrator_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "AZURE_CLIENT_ID"
        value = data.azurerm_user_assigned_identity.database_migrator.client_id
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
        name  = "POSTGRESQL_MIGRATOR_USER"
        value = data.azurerm_user_assigned_identity.database_migrator.name
      }

      env {
        name  = "POSTGRESQL_RUNTIME_PRINCIPAL_NAME"
        value = azurerm_user_assigned_identity.this["orchestrator"].name
      }

      env {
        name  = "POSTGRESQL_RUNTIME_PRINCIPAL_ID"
        value = azurerm_user_assigned_identity.this["orchestrator"].principal_id
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.database_migrator_acr_pull,
  ]
}
