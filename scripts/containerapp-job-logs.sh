#!/usr/bin/env bash

show_containerapp_job_execution_logs() {
  local job_name="$1"
  local resource_group="$2"
  local execution_name="$3"
  local log_label="$4"
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
    echo "Unable to determine the Container Apps environment for execution logs." >&2
    return
  fi

  workspace_id="$(
    az resource show \
      --ids "${environment_id}" \
      --query properties.appLogsConfiguration.logAnalyticsConfiguration.customerId \
      --output tsv 2>/dev/null || true
  )"

  if [[ -z "${workspace_id}" ]]; then
    echo "Execution logs are unavailable because the environment has no Log Analytics workspace." >&2
    return
  fi

  echo "${log_label}:" >&2

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

  echo "No execution logs were available after waiting for Log Analytics ingestion." >&2
}
