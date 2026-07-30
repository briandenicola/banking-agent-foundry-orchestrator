#!/usr/bin/env bash
set -euo pipefail

job_name="$(terraform -chdir=./apps output -raw DATABASE_MIGRATOR_JOB_NAME)"
resource_group="$(terraform -chdir=./apps output -raw APPS_RESOURCE_GROUP_NAME)"

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
