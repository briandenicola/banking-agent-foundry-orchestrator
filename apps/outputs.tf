output "ORCHESTRATOR_URL" {
  description = <<-EOT
    Orchestrator base URL. Ingress is internal, so this is the
    environment-internal FQDN (contains ".internal.") and is NOT routable from
    an operator workstation. Consumers outside the Container Apps environment
    must go through the Web UI. smoke-mvp.py detects the ".internal." marker and
    adapts its checks accordingly.
  EOT
  value       = local.orchestrator_internal_url
}

output "WEBUI_URL" {
  value = "https://${azurerm_container_app.webui.ingress[0].fqdn}"
}

output "AGENT_DEPLOYER_JOB_NAME" {
  value = azurerm_container_app_job.agent_deployer.name
}

output "DATABASE_MIGRATOR_JOB_NAME" {
  value = azurerm_container_app_job.database_migrator.name
}

output "APPS_RESOURCE_GROUP_NAME" {
  value = azurerm_resource_group.this.name
}

output "ORCHESTRATOR_TOKEN_SCOPE" {
  description = "Token scope the Web UI requests when service authentication is enabled; otherwise empty."
  value       = local.orchestrator_token_scope
}
