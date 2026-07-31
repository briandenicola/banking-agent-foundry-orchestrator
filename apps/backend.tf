# Remote state backend for the apps (Container Apps) stack.
#
# See docs/remote-state.md and infrastructure/backend.tf for the full rationale.
#
# Partial backend config used by deploy-production.yml:
#   -backend-config="resource_group_name=<TF_BACKEND_RESOURCE_GROUP>"
#   -backend-config="storage_account_name=<TF_BACKEND_STORAGE_ACCOUNT>"
#   -backend-config="container_name=<TF_BACKEND_CONTAINER>"
#   -backend-config="key=<environment>/apps.tfstate"
#
# Production state key:
#   production/apps.tfstate
terraform {
  backend "azurerm" {
    use_azuread_auth = true
  }
}
