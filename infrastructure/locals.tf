locals {
  location      = var.region
  resource_name = "${random_pet.this.id}-${random_id.this.dec}"

  resource_group_name       = "${local.resource_name}-rg"
  log_analytics_name        = "${local.resource_name}-logs"
  application_insights_name = "${local.resource_name}-appinsights"
  aca_name                  = "${local.resource_name}-env"
  acr_account_name          = "${replace(local.resource_name, "-", "")}acr"
  postgresql_server_name    = "${local.resource_name}-db"
  postgresql_database_name  = "banking_agent"
  foundry_name              = "${local.resource_name}-foundry"
  foundry_project_name      = "${local.resource_name}-project"
  model_deployment_name     = "gpt-5.4-mini"
  model_version             = "2026-03-17"
  # Foundry memory stores require an embedding model deployment alongside the
  # chat model. GlobalStandard is the only shared SKU offered for this model in
  # the supported regions; plain Standard is not available.
  embedding_deployment_name = "text-embedding-3-small"
  embedding_model_version   = "1"

  tags = {
    Application = "Banking Agent"
    ManagedBy   = "Terraform"
  }
}
