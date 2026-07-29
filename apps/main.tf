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
  agents_app_name       = "${var.app_name}-agents"
  litellm_app_name      = "${var.app_name}-litellm"

  orchestrator_image = "${data.azurerm_container_registry.this.login_server}/orchestrator:${var.image_tag}"
  webui_image        = "${data.azurerm_container_registry.this.login_server}/webui:${var.image_tag}"
  agents_image       = "${data.azurerm_container_registry.this.login_server}/agents:${var.image_tag}"
  litellm_image      = "${data.azurerm_container_registry.this.login_server}/litellm:${var.image_tag}"

  foundry_project_endpoint = "https://${local.foundry_name}.services.ai.azure.com/api/projects/${local.foundry_project}"
  foundry_openai_endpoint  = "https://${local.foundry_name}.openai.azure.com"
  postgresql_host          = "${var.app_name}-db.postgres.database.azure.com"
  postgresql_database      = "banking_agent"
  litellm_internal_url     = "https://${azurerm_container_app.litellm.ingress[0].fqdn}"
  agents_internal_url      = "https://${azurerm_container_app.agents.ingress[0].fqdn}"

  foundry_tool_endpoints = jsonencode({
    "workflow.plan"       = "${local.agents_internal_url}/plan"
    "transaction.explain" = "${local.agents_internal_url}/transaction-explanation"
    "suspicious.assess"   = "${local.agents_internal_url}/suspicious-activity"
    "dispute.plan"        = "${local.agents_internal_url}/dispute"
  })

  tags = {
    Application = "Banking Agent"
    ManagedBy   = "Terraform"
  }
}
