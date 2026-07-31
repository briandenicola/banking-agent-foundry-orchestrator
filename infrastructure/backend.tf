# Remote state backend for the infrastructure stack.
#
# Configuration is supplied at runtime via -backend-config flags (see docs/remote-state.md).
# CI runs terraform init -backend=false for fast validation without credentials.
#
# Partial backend config used by deploy-production.yml:
#   -backend-config="resource_group_name=<TF_BACKEND_RESOURCE_GROUP>"
#   -backend-config="storage_account_name=<TF_BACKEND_STORAGE_ACCOUNT>"
#   -backend-config="container_name=<TF_BACKEND_CONTAINER>"
#   -backend-config="key=<environment>/infrastructure.tfstate"
#
# Per-environment state isolation is achieved through an environment-prefixed key.
# Production uses production/infrastructure.tfstate; other environments use their
# own prefix and GitHub Environment.
#
# Azure Blob storage provides automatic optimistic-locking via blob leases, so no
# additional DynamoDB-style lock table is required.
terraform {
  backend "azurerm" {
    use_azuread_auth = true
    # All other fields (resource_group_name, storage_account_name, container_name,
    # key, and CI's use_oidc flag) are supplied via -backend-config at init time so
    # local Azure CLI authentication and GitHub OIDC both work without credentials
    # in source control.
  }
}
