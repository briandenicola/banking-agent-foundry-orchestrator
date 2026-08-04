#!/usr/bin/env bash
# bootstrap-remote-state.sh
#
# One-time setup for the Azure Blob Terraform remote-state backend.
# Run ONCE per subscription before the first 'terraform init' with a real backend.
# Safe to re-run: all az commands are idempotent.
#
# Prerequisites:
#   - Azure CLI (az) authenticated with an identity that can create resource groups
#     and storage accounts and assign roles in the target subscription.
#   - No long-lived credentials; use 'az login --use-device-code'.
#
# Usage:
#   ./scripts/bootstrap-remote-state.sh \
#     --subscription  <subscription-id>  \
#     --resource-group tfstate-rg        \
#     --storage-account <unique-name>    \
#     --container tfstate                \
#     --location swedencentral           \
#     [--caller-object-id <service-principal-object-id>]
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
CALLER_OBJECT_ID=""
CALLER_PRINCIPAL_TYPE=""
PRIVATE_ENDPOINT_SUBNET_ID=""
PRIVATE_DNS_ZONE_ID=""

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
    --caller-object-id) CALLER_OBJECT_ID="$2"; shift 2 ;;
    --private-endpoint-subnet-id) PRIVATE_ENDPOINT_SUBNET_ID="$2"; shift 2 ;;
    --private-dns-zone-id) PRIVATE_DNS_ZONE_ID="$2"; shift 2 ;;
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
  --public-network-access Enabled \
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

if [[ -n "$PRIVATE_ENDPOINT_SUBNET_ID" || -n "$PRIVATE_DNS_ZONE_ID" ]]; then
  if [[ -z "$PRIVATE_ENDPOINT_SUBNET_ID" || -z "$PRIVATE_DNS_ZONE_ID" ]]; then
    echo "ERROR: --private-endpoint-subnet-id and --private-dns-zone-id must be supplied together." >&2
    exit 1
  fi

  PRIVATE_ENDPOINT_NAME="${STORAGE_ACCOUNT}-blob-pe"
  echo "Creating storage private endpoint: $PRIVATE_ENDPOINT_NAME"
  az network private-endpoint create \
    --name "$PRIVATE_ENDPOINT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --location "$LOCATION" \
    --subnet "$PRIVATE_ENDPOINT_SUBNET_ID" \
    --private-connection-resource-id "$STORAGE_ACCOUNT_ID" \
    --group-id blob \
    --connection-name "${STORAGE_ACCOUNT}-blob" \
    --output none

  echo "Linking the storage private endpoint to private DNS"
  az network private-endpoint dns-zone-group create \
    --name blob \
    --endpoint-name "$PRIVATE_ENDPOINT_NAME" \
    --resource-group "$RESOURCE_GROUP" \
    --private-dns-zone "$PRIVATE_DNS_ZONE_ID" \
    --zone-name blob \
    --output none
fi

ACCOUNT_TYPE=$(az account show --query user.type --output tsv)
if [[ "$ACCOUNT_TYPE" == "user" ]]; then
  CALLER_OBJECT_ID=$(az ad signed-in-user show --query id --output tsv)
  CALLER_PRINCIPAL_TYPE="User"
elif [[ -n "$CALLER_OBJECT_ID" ]]; then
  CALLER_PRINCIPAL_TYPE="ServicePrincipal"
else
  echo "ERROR: Non-user Azure CLI authentication requires --caller-object-id." >&2
  echo "Pass the service principal or managed identity object ID that will run Terraform." >&2
  exit 1
fi

echo "Granting the current Azure CLI identity access to Terraform state"
az role assignment create \
  --assignee-object-id "$CALLER_OBJECT_ID" \
  --assignee-principal-type "$CALLER_PRINCIPAL_TYPE" \
  --role "Storage Blob Data Contributor" \
  --scope "$STORAGE_ACCOUNT_ID" \
  --output none

echo "Waiting for Terraform state data-plane access"
for _ in $(seq 1 30); do
  if az storage container show \
    --account-name "$STORAGE_ACCOUNT" \
    --name "$CONTAINER" \
    --auth-mode login \
    --output none 2>/dev/null; then
    break
  fi
  sleep 10
done

if ! az storage container show \
  --account-name "$STORAGE_ACCOUNT" \
  --name "$CONTAINER" \
  --auth-mode login \
  --output none 2>/dev/null; then
  echo "ERROR: Timed out waiting for access to the Terraform state container." >&2
  exit 1
fi

echo ""
echo "Bootstrap complete. Set these as GitHub production environment secrets:"
echo "  TF_BACKEND_RESOURCE_GROUP  = $RESOURCE_GROUP"
echo "  TF_BACKEND_STORAGE_ACCOUNT = $STORAGE_ACCOUNT"
echo "  TF_BACKEND_CONTAINER       = $CONTAINER"
echo ""
echo "The current Azure CLI identity can now read and write Terraform state."
echo ""
echo "Assign the deployment SP 'Storage Blob Data Contributor' on the storage account:"
echo "  az role assignment create \\"
echo "    --assignee <SP_OBJECT_ID> \\"
echo "    --role 'Storage Blob Data Contributor' \\"
echo "    --scope \$(az storage account show --name $STORAGE_ACCOUNT --resource-group $RESOURCE_GROUP --query id -o tsv)"
