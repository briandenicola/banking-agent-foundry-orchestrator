#!/usr/bin/env bash
set -euo pipefail

job_name="$(terraform -chdir=./apps output -raw AGENT_DEPLOYER_JOB_NAME)"
resource_group="$(terraform -chdir=./apps output -raw APPS_RESOURCE_GROUP_NAME)"

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
      exit 0
      ;;
    Failed)
      echo "Hosted Agent deployment execution ${execution_name} failed." >&2
      exit 1
      ;;
    *)
      echo "Hosted Agent deployment status: ${status}"
      sleep 10
      ;;
  esac
done

echo "Timed out waiting for Hosted Agent deployment execution ${execution_name}." >&2
exit 1
