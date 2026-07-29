output "resource_group_name" {
  value = azurerm_resource_group.rg.name
}

output "container_app_environment_name" {
  value = azurerm_container_app_environment.cae.name
}

output "orchestrator_url" {
  value = azurerm_container_app.orchestrator.latest_revision_fqdn
}

output "agents_url" {
  value = azurerm_container_app.agents.latest_revision_fqdn
}
