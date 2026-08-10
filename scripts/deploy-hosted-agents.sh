#!/usr/bin/env bash
set -euo pipefail

source "$(dirname "${BASH_SOURCE[0]}")/containerapp-job-logs.sh"
source "$(dirname "${BASH_SOURCE[0]}")/guard-stack-alignment.sh"

assert_stacks_aligned

job_name="$(terraform -chdir=./apps output -raw AGENT_DEPLOYER_JOB_NAME)"
resource_group="$(terraform -chdir=./apps output -raw APPS_RESOURCE_GROUP_NAME)"

configure_model_access() {
  local foundry_account_name
  local foundry_resource_group
  local foundry_account_id
  local foundry_project_endpoint
  local agent_definitions
  local versions
  local principal_id

  foundry_account_name="$(terraform -chdir=./infrastructure output -raw FOUNDRY_ACCOUNT_NAME)"
  foundry_resource_group="$(terraform -chdir=./infrastructure output -raw RESOURCE_GROUP_NAME)"
  foundry_project_endpoint="$(terraform -chdir=./infrastructure output -raw FOUNDRY_PROJECT_ENDPOINT)"
  foundry_account_id="$(
    az cognitiveservices account show \
      --name "${foundry_account_name}" \
      --resource-group "${foundry_resource_group}" \
      --query id \
      --output tsv
  )"
  agent_definitions="$(
    az containerapp job show \
      --name "${job_name}" \
      --resource-group "${resource_group}" \
      --query "properties.template.containers[0].env[?name=='AGENT_DEFINITIONS'].value | [0]" \
      --output tsv
  )"

  while read -r agent_name; do
    versions="$(
      az rest \
        --method GET \
        --url "${foundry_project_endpoint}/agents/${agent_name}/versions?api-version=v1" \
        --resource "https://ai.azure.com" \
        --output json
    )"
    principal_id="$(
      jq -r '
        [
          (.data // .value // .items // [])[]
          | select((.status | ascii_downcase) == "active" or (.status | ascii_downcase) == "running")
        ]
        | sort_by(.created_at)
        | reverse
        | .[0].instance_identity.principal_id // empty
      ' <<<"${versions}"
    )"

    if [[ -z "${principal_id}" ]]; then
      echo "Unable to resolve the active instance identity for Hosted Agent ${agent_name}." >&2
      return 1
    fi

    if [[ "$(
      az role assignment list \
        --assignee "${principal_id}" \
        --role "Cognitive Services OpenAI User" \
        --scope "${foundry_account_id}" \
        --query "length(@)" \
        --output tsv
    )" == "0" ]]; then
      az role assignment create \
        --assignee-object-id "${principal_id}" \
        --assignee-principal-type ServicePrincipal \
        --role "Cognitive Services OpenAI User" \
        --scope "${foundry_account_id}" \
        --only-show-errors \
        --output none
      echo "${agent_name}: granted model invocation access to ${principal_id}."
    else
      echo "${agent_name}: model invocation access already exists for ${principal_id}."
    fi
  done < <(jq -r ".[].name" <<<"${agent_definitions}")
}

execution_name="$(
  az containerapp job start \
    --name "${job_name}" \
    --resource-group "${resource_group}" \
    --query name \
    --output tsv
)"

if [[ -z "${execution_name}" ]]; then
  echo "Container Apps did not return a Hosted Agent deployment execution name." >&2
  exit 1
fi

echo "Started Hosted Agent deployment execution ${execution_name}."

for _ in $(seq 1 90); do
  status="$(
    az containerapp job execution show \
      --name "${job_name}" \
      --resource-group "${resource_group}" \
      --job-execution-name "${execution_name}" \
      --query properties.status \
      --output tsv
  )"

  case "${status}" in
    Succeeded)
      echo "Hosted Agent deployment completed successfully."
      configure_model_access
      exit 0
      ;;
    Failed)
      echo "Hosted Agent deployment execution ${execution_name} failed." >&2
      show_containerapp_job_execution_logs \
        "${job_name}" \
        "${resource_group}" \
        "${execution_name}" \
        "Hosted Agent deployment logs"
      exit 1
      ;;
    *)
      echo "Hosted Agent deployment status: ${status}"
      sleep 10
      ;;
  esac
done

echo "Timed out waiting for Hosted Agent deployment execution ${execution_name}." >&2
show_containerapp_job_execution_logs \
  "${job_name}" \
  "${resource_group}" \
  "${execution_name}" \
  "Hosted Agent deployment logs"
exit 1
