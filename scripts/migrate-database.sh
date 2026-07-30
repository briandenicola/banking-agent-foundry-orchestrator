#!/usr/bin/env bash
set -euo pipefail

job_name="$(terraform -chdir=./apps output -raw DATABASE_MIGRATOR_JOB_NAME)"
resource_group="$(terraform -chdir=./apps output -raw APPS_RESOURCE_GROUP_NAME)"

show_execution_logs() {
  local environment_id
  local workspace_id
  local logs

  environment_id="$(
    az containerapp job show \
      --name "${job_name}" \
      --resource-group "${resource_group}" \
      --query properties.environmentId \
      --output tsv 2>/dev/null || true
  )"

  if [[ -z "${environment_id}" ]]; then
    echo "Unable to determine the Container Apps environment for migration logs." >&2
    return
  fi

  workspace_id="$(
    az resource show \
      --ids "${environment_id}" \
      --query properties.appLogsConfiguration.logAnalyticsConfiguration.customerId \
      --output tsv 2>/dev/null || true
  )"

  if [[ -z "${workspace_id}" ]]; then
    echo "Migration logs are unavailable because the environment has no Log Analytics workspace." >&2
    return
  fi

  echo "Database migration logs:" >&2

  for _ in $(seq 1 3); do
    logs="$(
      az monitor log-analytics query \
        --workspace "${workspace_id}" \
        --analytics-query "ContainerAppConsoleLogs_CL | where ContainerGroupName_s startswith '${execution_name}-' | project TimeGenerated, Log_s | order by TimeGenerated asc" \
        --query "[].Log_s" \
        --output tsv 2>/dev/null || true
    )"

    if [[ -n "${logs}" ]]; then
      printf '%s\n' "${logs}" >&2
      return
    fi

    sleep 10
  done

  echo "No migration logs were available after waiting for Log Analytics ingestion." >&2
}

execution_name="$(
  az containerapp job start \
    --name "${job_name}" \
    --resource-group "${resource_group}" \
    --query name \
    --output tsv
)"

if [[ -z "${execution_name}" ]]; then
  echo "Container Apps did not return a database migration execution name." >&2
  exit 1
fi

echo "Started database migration execution ${execution_name}."

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
      echo "Database migration completed successfully."
      exit 0
      ;;
    Failed)
      echo "Database migration execution ${execution_name} failed." >&2
      show_execution_logs
      exit 1
      ;;
    *)
      echo "Database migration status: ${status}"
      sleep 10
      ;;
  esac
done

echo "Timed out waiting for database migration execution ${execution_name}." >&2
exit 1
