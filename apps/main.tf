locals {
  core_rg_name     = "${var.app_name}-rg"
  apps_rg_name     = "${var.app_name}-apps-rg"
  acr_name         = "${replace(var.app_name, "-", "")}acr"
  cae_name         = "${var.app_name}-env"
  foundry_name     = "${var.app_name}-foundry"
  foundry_project  = "${var.app_name}-project"
  model_deployment = "gpt-5.4-mini"

  orchestrator_app_name = "${var.app_name}-orchestrator"
  webui_app_name        = "${var.app_name}-webui"

  orchestrator_image      = "${data.azurerm_container_registry.this.login_server}/orchestrator:${var.image_tag}"
  webui_image             = "${data.azurerm_container_registry.this.login_server}/webui:${var.image_tag}"
  hosted_agents_image     = "${data.azurerm_container_registry.this.login_server}/hosted-agents:${var.image_tag}"
  agent_deployer_image    = "${data.azurerm_container_registry.this.login_server}/agent-deployer:${var.image_tag}"
  database_migrator_image = "${data.azurerm_container_registry.this.login_server}/database-migrator:${var.image_tag}"

  foundry_project_endpoint = "https://${local.foundry_name}.services.ai.azure.com/api/projects/${local.foundry_project}"
  foundry_openai_endpoint  = "https://${local.foundry_name}.openai.azure.com"
  postgresql_host          = "${var.app_name}-db.postgres.database.azure.com"
  postgresql_database      = "banking_agent"

  foundry_tool_endpoints = jsonencode({
    "workflow.plan"       = "${local.foundry_project_endpoint}/agents/workflow-planning/endpoint/protocols/invocations?api-version=v1"
    "transaction.explain" = "${local.foundry_project_endpoint}/agents/transaction-explanation/endpoint/protocols/invocations?api-version=v1"
    "suspicious.assess"   = "${local.foundry_project_endpoint}/agents/suspicious-activity/endpoint/protocols/invocations?api-version=v1"
    "dispute.plan"        = "${local.foundry_project_endpoint}/agents/dispute-planning/endpoint/protocols/invocations?api-version=v1"
  })

  foundry_mcp_tool_endpoints = jsonencode({
    "transaction.explain" = "${local.foundry_project_endpoint}/agents/transaction-explanation/endpoint/protocols/invocations?api-version=v1"
  })

  hosted_agent_definitions = [
    {
      name = "workflow-planning"
      kind = "workflow-planning"
    },
    {
      name = "transaction-explanation"
      kind = "transaction-explanation"
    },
    {
      name = "suspicious-activity"
      kind = "suspicious-activity"
    },
    {
      name = "dispute-planning"
      kind = "dispute-planning"
    }
  ]

  tags = {
    Application = "Banking Agent"
    ManagedBy   = "Terraform"
  }
}

data "azurerm_user_assigned_identity" "database_migrator" {
  name                = "${var.app_name}-database-migrator-identity"
  resource_group_name = local.core_rg_name
}
