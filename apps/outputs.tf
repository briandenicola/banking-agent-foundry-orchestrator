output "ORCHESTRATOR_URL" {
  value = "https://${azurerm_container_app.orchestrator.ingress[0].fqdn}"
}

output "WEBUI_URL" {
  value = "https://${azurerm_container_app.webui.ingress[0].fqdn}"
}

output "LITELLM_INTERNAL_FQDN" {
  value = azurerm_container_app.litellm.ingress[0].fqdn
}

output "AGENT_DEPLOYER_JOB_NAME" {
  value = azurerm_container_app_job.agent_deployer.name
}

output "APPS_RESOURCE_GROUP_NAME" {
  value = azurerm_resource_group.this.name
}
