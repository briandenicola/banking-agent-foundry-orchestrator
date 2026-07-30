output "APP_NAME" {
  description = "Base random resource name consumed by the apps Terraform stack."
  value       = local.resource_name
}

output "RESOURCE_GROUP_NAME" {
  value = azurerm_resource_group.this.name
}

output "ACR_NAME" {
  value = azurerm_container_registry.this.name
}

output "ACR_LOGIN_SERVER" {
  value = azurerm_container_registry.this.login_server
}

output "CONTAINER_APP_ENVIRONMENT_NAME" {
  value = azurerm_container_app_environment.this.name
}

output "APPLICATION_INSIGHTS_CONNECTION_STRING" {
  value     = azurerm_application_insights.this.connection_string
  sensitive = true
}

output "FOUNDRY_ACCOUNT_NAME" {
  value = local.foundry_name
}

output "FOUNDRY_PROJECT_NAME" {
  value = local.foundry_project_name
}

output "FOUNDRY_PROJECT_ENDPOINT" {
  value = "https://${local.foundry_name}.services.ai.azure.com/api/projects/${local.foundry_project_name}"
}

output "MODEL_DEPLOYMENT_NAME" {
  value = local.model_deployment_name
}

output "POSTGRESQL_SERVER_NAME" {
  value = azurerm_postgresql_flexible_server.this.name
}

output "POSTGRESQL_HOST" {
  value = azurerm_postgresql_flexible_server.this.fqdn
}

output "POSTGRESQL_DATABASE" {
  value = azurerm_postgresql_flexible_server_database.this.name
}

output "DATABASE_MIGRATOR_IDENTITY_NAME" {
  value = azurerm_user_assigned_identity.database_migrator.name
}

output "DATABASE_MIGRATOR_IDENTITY_ID" {
  value = azurerm_user_assigned_identity.database_migrator.id
}

output "DATABASE_MIGRATOR_CLIENT_ID" {
  value = azurerm_user_assigned_identity.database_migrator.client_id
}

output "DATABASE_MIGRATOR_PRINCIPAL_ID" {
  value = azurerm_user_assigned_identity.database_migrator.principal_id
}
