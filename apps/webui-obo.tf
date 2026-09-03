# On-behalf-of support for the Web UI.
#
# Why a storage account appears here
# ----------------------------------
# The exchange needs the signed-in user's access token, and Container Apps only
# hands that to the container when Easy Auth's token store is enabled. The token
# store is backed by blob storage and is configured with a SAS URL, so turning
# OBO on necessarily provisions a storage account. Everything in this file is
# absent when `enable_obo` is false, which is the default.
#
# The SAS is a key at rest, which the repository otherwise avoids in favour of
# managed identity. Container Apps offers no identity-based configuration for
# the token store, so the choice is a SAS or no OBO. It is scoped to one blob
# container and expires, and the account holds nothing but Easy Auth's own
# session tokens.

locals {
  # OBO depends on sign-in: without Easy Auth there is no user token to exchange.
  obo_enabled = var.enable_obo && local.webui_auth_enabled && var.obo_app_id != ""

  token_store_secret_name    = "token-store-sas-url"
  token_store_container_name = "easyauth-tokens"

  # Storage account names allow only lowercase alphanumerics, and cap at 24
  # characters.
  token_store_account_name = substr("${replace(var.app_name, "-", "")}tokens", 0, 24)

  token_store_sas_start = formatdate("YYYY-01-01'T'00:00:00Z", timestamp())

  orchestrator_obo_scope = local.obo_enabled ? "api://${var.obo_app_id}/user_impersonation" : ""
}

resource "azurerm_storage_account" "token_store" {
  count = local.obo_enabled ? 1 : 0

  name                     = local.token_store_account_name
  resource_group_name      = azurerm_resource_group.this.name
  location                 = azurerm_resource_group.this.location
  account_tier             = "Standard"
  account_replication_type = "LRS"

  # The container is private and reached only through the SAS below.
  allow_nested_items_to_be_public = false
  min_tls_version                 = "TLS1_2"

  tags = local.tags
}

resource "azurerm_storage_container" "token_store" {
  count = local.obo_enabled ? 1 : 0

  name                  = local.token_store_container_name
  storage_account_id    = azurerm_storage_account.token_store[0].id
  container_access_type = "private"
}

data "azurerm_storage_account_blob_container_sas" "token_store" {
  count = local.obo_enabled ? 1 : 0

  connection_string = azurerm_storage_account.token_store[0].primary_connection_string
  container_name    = azurerm_storage_container.token_store[0].name

  # Anchored to the start of the calendar year rather than to now(). A bare
  # timestamp() would produce a different SAS on every plan, rewriting the
  # container app secret and restarting the Web UI on every apply. Anchoring
  # keeps the value stable for a year and then rotates it once.
  start  = local.token_store_sas_start
  expiry = timeadd(local.token_store_sas_start, "17520h") # two years

  permissions {
    read   = true
    add    = true
    create = true
    write  = true
    delete = true
    list   = true
  }

}
