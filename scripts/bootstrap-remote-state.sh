#!/usr/bin/env bash
# bootstrap-remote-state.sh
#
# One-time setup for the Azure Blob Terraform remote-state backend.
# Run ONCE per subscription before the first 'terraform init' with a real backend.
# Safe to re-run: all az commands are idempotent.
#
# Prerequisites:
#   - Azure CLI (az) authenticated with an identity that can create resource groups
#     and storage accounts in the target subscription.
#   - No long-lived credentials; use 'az login --use-device-code' or managed identity.
#
# Usage:
#   ./scripts/bootstrap-remote-state.sh \
#     --subscription  <subscription-id>  \
#     --resource-group tfstate-rg        \
#     --storage-account <unique-name>    \
#     --container tfstate                \
#     --location swedencentral
#
# The resulting values become GitHub production environment secrets:
#   TF_BACKEND_RESOURCE_GROUP  → --resource-group value
#   TF_BACKEND_STORAGE_ACCOUNT → --storage-account value
#   TF_BACKEND_CONTAINER       → --container value

set -euo pipefail

SUBSCRIPTION=""
RESOURCE_GROUP="tfstate-rg"
STORAGE_ACCOUNT=""
CONTAINER="tfstate"
LOCATION="swedencentral"

usage() {
  grep '^#' "$0" | sed 's/^# \{0,1\}//'
  exit 1
}

while [[ $# -gt 0 ]]; do
  case "$1" in
    --subscription)   SUBSCRIPTION="$2";   shift 2 ;;
    --resource-group) RESOURCE_GROUP="$2"; shift 2 ;;
    --storage-account) STORAGE_ACCOUNT="$2"; shift 2 ;;
    --container)      CONTAINER="$2";      shift 2 ;;
    --location)       LOCATION="$2";       shift 2 ;;
    *) usage ;;
  esac
done

if [[ -z "$SUBSCRIPTION" || -z "$STORAGE_ACCOUNT" ]]; then
  echo "ERROR: --subscription and --storage-account are required." >&2
  usage
fi

echo "Setting active subscription: $SUBSCRIPTION"
az account set --subscription "$SUBSCRIPTION"

echo "Creating resource group: $RESOURCE_GROUP ($LOCATION)"
az group create \
  --name "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --output none

echo "Creating storage account: $STORAGE_ACCOUNT"
az storage account create \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --location "$LOCATION" \
  --sku Standard_LRS \
  --kind StorageV2 \
  --allow-blob-public-access false \
  --allow-shared-key-access false \
  --min-tls-version TLS1_2 \
  --https-only true \
  --output none

echo "Enabling versioning and soft-delete on storage account"
az storage account blob-service-properties update \
  --account-name "$STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --enable-versioning true \
  --enable-delete-retention true \
  --delete-retention-days 30 \
  --output none

echo "Creating blob container through the Azure management plane: $CONTAINER"
STORAGE_ACCOUNT_ID=$(az storage account show \
  --name "$STORAGE_ACCOUNT" \
  --resource-group "$RESOURCE_GROUP" \
  --query id \
  --output tsv)
az resource create \
  --id "$STORAGE_ACCOUNT_ID/blobServices/default/containers/$CONTAINER" \
  --api-version "2023-05-01" \
  --properties '{}' \
  --output none

echo ""
echo "Bootstrap complete. Set these as GitHub production environment secrets:"
echo "  TF_BACKEND_RESOURCE_GROUP  = $RESOURCE_GROUP"
echo "  TF_BACKEND_STORAGE_ACCOUNT = $STORAGE_ACCOUNT"
echo "  TF_BACKEND_CONTAINER       = $CONTAINER"
echo ""
echo "Assign the deployment SP 'Storage Blob Data Contributor' on the storage account:"
echo "  az role assignment create \\"
echo "    --assignee <SP_OBJECT_ID> \\"
echo "    --role 'Storage Blob Data Contributor' \\"
echo "    --scope \$(az storage account show --name $STORAGE_ACCOUNT --resource-group $RESOURCE_GROUP --query id -o tsv)"
