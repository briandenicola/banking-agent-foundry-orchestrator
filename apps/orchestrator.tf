resource "azurerm_container_app" "orchestrator" {
  name                         = local.orchestrator_app_name
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  resource_group_name          = azurerm_resource_group.this.name
  revision_mode                = "Single"
  workload_profile_name        = "Consumption"
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

      startup_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        interval_seconds        = 5
        timeout                 = 3
        failure_count_threshold = 30
      }

      liveness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/live"
        initial_delay           = 10
        interval_seconds        = 15
        timeout                 = 3
        failure_count_threshold = 3
      }

      readiness_probe {
        transport               = "HTTP"
        port                    = 8080
        path                    = "/health/ready"
        interval_seconds        = 10
        timeout                 = 5
        failure_count_threshold = 3
        success_count_threshold = 1
      }

      env {
        name  = "ASPNETCORE_URLS"
        value = "http://+:8080"
      }

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.this["orchestrator"].client_id
      }

      env {
        name  = "SERVICE_AUTH_ENABLED"
        value = tostring(var.enable_service_auth)
      }

      env {
        name  = "ALLOW_INSECURE_SERVICE_AUTH"
        value = tostring(var.allow_insecure_service_auth)
      }

      env {
        name  = "DEMO_SCENARIOS_ENABLED"
        value = "true"
      }

      env {
        name  = "WORKFLOW_RECOVERY_SCAN_INTERVAL_SECONDS"
        value = "30"
      }

      env {
        name  = "WORKFLOW_RECOVERY_STALE_AFTER_SECONDS"
        value = "120"
      }

      env {
        name  = "WORKFLOW_RECOVERY_BATCH_SIZE"
        value = "10"
      }

      env {
        name  = "WORKFLOW_RECOVERY_MAX_ATTEMPTS"
        value = "5"
      }

      env {
        name  = "WORKFLOW_RECOVERY_BACKOFF_BASE_SECONDS"
        value = "30"
      }

      env {
        name  = "WORKFLOW_RECOVERY_BACKOFF_MAX_SECONDS"
        value = "900"
      }

      env {
        name  = "AZURE_TENANT_ID"
        value = var.enable_service_auth ? data.azurerm_client_config.current.tenant_id : ""
      }

      env {
        name  = "ORCHESTRATOR_APP_ID"
        value = var.enable_service_auth ? azuread_application.orchestrator_api[0].client_id : ""
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
        name  = "FOUNDRY_MCP_TOOL_ENDPOINTS"
        value = local.foundry_mcp_tool_endpoints
      }

      env {
        name  = "FOUNDRY_MAX_ATTEMPTS"
        value = "3"
      }

      env {
        name  = "FOUNDRY_ATTEMPT_TIMEOUT_SECONDS"
        value = "30"
      }

      env {
        name  = "FOUNDRY_RETRY_BASE_DELAY_MILLISECONDS"
        value = "250"
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

  lifecycle {
    precondition {
      condition     = var.enable_service_auth || var.allow_insecure_service_auth
      error_message = "enable_service_auth=false requires allow_insecure_service_auth=true. Disabling service authentication leaves workflow endpoints open to any caller that can reach the ingress, so it must be acknowledged explicitly. The orchestrator enforces the same rule at startup and will refuse to boot otherwise."
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.orchestrator_agent_consumer,
  ]
}
