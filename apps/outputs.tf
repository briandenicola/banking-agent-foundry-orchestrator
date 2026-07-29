output "ORCHESTRATOR_URL" {
  value = "https://${azurerm_container_app.orchestrator.ingress[0].fqdn}"
}

output "WEBUI_URL" {
  value = "https://${azurerm_container_app.webui.ingress[0].fqdn}"
}

output "AGENTS_INTERNAL_FQDN" {
  value = azurerm_container_app.agents.ingress[0].fqdn
}

output "LITELLM_INTERNAL_FQDN" {
  value = azurerm_container_app.litellm.ingress[0].fqdn
}
