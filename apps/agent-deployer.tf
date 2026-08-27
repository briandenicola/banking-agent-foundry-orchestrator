resource "azurerm_container_app_job" "agent_deployer" {
  name                         = "${var.app_name}-agent-deployer"
  resource_group_name          = azurerm_resource_group.this.name
  workload_profile_name        = "Consumption"
  location                     = azurerm_resource_group.this.location
  container_app_environment_id = data.azurerm_container_app_environment.this.id
  replica_timeout_in_seconds   = 900
  replica_retry_limit          = 2
  tags                         = local.tags

  identity {
    type         = "UserAssigned"
    identity_ids = [azurerm_user_assigned_identity.this["agent-deployer"].id]
  }

  registry {
    server   = data.azurerm_container_registry.this.login_server
    identity = azurerm_user_assigned_identity.this["agent-deployer"].id
  }

  manual_trigger_config {
    parallelism              = 1
    replica_completion_count = 1
  }

  template {
    container {
      name   = "agent-deployer"
      image  = local.agent_deployer_image
      cpu    = 0.5
      memory = "1Gi"

      env {
        name  = "AZURE_CLIENT_ID"
        value = azurerm_user_assigned_identity.this["agent-deployer"].client_id
      }

      env {
        name  = "BANKING_AGENT_PROJECT_ENDPOINT"
        value = local.foundry_project_endpoint
      }

      env {
        name  = "HOSTED_AGENT_IMAGE"
        value = local.hosted_agents_image
      }

      env {
        name  = "AZURE_AI_MODEL_DEPLOYMENT_NAME"
        value = local.model_deployment
      }

      env {
        name  = "AGENT_DEFINITIONS"
        value = jsonencode(local.hosted_agent_definitions)
      }

      env {
        name  = "AZURE_AI_EMBEDDING_DEPLOYMENT_NAME"
        value = local.embedding_deployment
      }

      env {
        name  = "MEMORY_STORE_NAME"
        value = local.memory_store_name
      }

      env {
        name  = "MEMORY_AGENT_NAME"
        value = local.memory_agent_name
      }

      env {
        name  = "MEMORY_USER_PROFILE_DETAILS"
        value = local.memory_user_profile_details
      }

      env {
        name  = "MEMORY_AGENT_INSTRUCTIONS"
        value = local.memory_agent_instructions
      }

      env {
        name  = "TOOLBOX_NAME"
        value = local.toolbox_name
      }

      env {
        name  = "TOOLBOX_TOOLS"
        value = jsonencode(local.toolbox_tools)
      }

      env {
        name  = "TOOLBOX_REQUIRE_APPROVAL"
        value = local.toolbox_require_approval
      }
    }
  }

  depends_on = [
    azurerm_role_assignment.acr_pull,
    azurerm_role_assignment.agent_deployer_project_manager,
    azurerm_role_assignment.foundry_project_acr_pull,
    azurerm_role_assignment.foundry_project_user,
  ]
}
