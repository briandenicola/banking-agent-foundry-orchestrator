#!/usr/bin/env bash
# Fails fast when the apps stack state does not describe the environment the
# infrastructure stack currently points at.
#
# Both scripts that start Container Apps Jobs resolve the job name and resource
# group from `terraform -chdir=./apps output`. When that state is stale - for
# example after an infrastructure rebuild whose teardown left the old apps state
# behind - those outputs still name the previous environment, so the job runs in
# the wrong resource group against the wrong database. The observed failure is a
# TCP timeout from a hostname that belongs to an environment that no longer
# exists, which gives no hint at the real cause.
#
# apps/main.tf derives every name from var.app_name, and app_name is supplied by
# the infrastructure output APP_NAME, so `<APP_NAME>-apps-rg` must always equal
# the APPS_RESOURCE_GROUP_NAME recorded in the apps state.

set -euo pipefail

assert_stacks_aligned() {
  local infrastructure_app_name
  local apps_resource_group
  local expected_apps_resource_group

  if ! infrastructure_app_name="$(terraform -chdir=./infrastructure output -raw APP_NAME 2>/dev/null)" \
    || [[ -z "${infrastructure_app_name}" ]]; then
    echo "ERROR: Could not read APP_NAME from the infrastructure stack." >&2
    echo "The infrastructure stack has not been applied. Run 'task cloud:up' first." >&2
    exit 1
  fi

  if ! apps_resource_group="$(terraform -chdir=./apps output -raw APPS_RESOURCE_GROUP_NAME 2>/dev/null)" \
    || [[ -z "${apps_resource_group}" ]]; then
    echo "ERROR: Could not read APPS_RESOURCE_GROUP_NAME from the apps stack." >&2
    echo "The apps stack has not been applied against ${infrastructure_app_name}." >&2
    echo "Run 'task app:apply -- <region>' before this task." >&2
    exit 1
  fi

  expected_apps_resource_group="${infrastructure_app_name}-apps-rg"

  if [[ "${apps_resource_group}" != "${expected_apps_resource_group}" ]]; then
    echo "ERROR: The apps stack state does not match the current infrastructure." >&2
    echo "  infrastructure APP_NAME:            ${infrastructure_app_name}" >&2
    echo "  expected apps resource group:       ${expected_apps_resource_group}" >&2
    echo "  apps state resource group:          ${apps_resource_group}" >&2
    echo "" >&2
    echo "The apps state is stale and still describes a previous environment." >&2
    echo "Running this task would target the wrong resource group and database." >&2
    echo "Run 'task app:apply -- <region>' to reconcile the apps stack first." >&2
    exit 1
  fi
}
