#!/usr/bin/env bash
set -euo pipefail

stack="${1:?Terraform stack directory is required.}"
legacy_state=()

if [[ -s "${stack}/terraform.tfstate" ]]; then
  legacy_state+=("${stack}/terraform.tfstate")
fi

shopt -s nullglob
for state_file in "${stack}"/terraform.tfstate.d/*/terraform.tfstate; do
  if [[ -s "${state_file}" ]]; then
    legacy_state+=("${state_file}")
  fi
done

if (( ${#legacy_state[@]} == 0 )); then
  exit 0
fi

echo "ERROR: Local Terraform state exists and must not be bypassed:" >&2
printf '  %s\n' "${legacy_state[@]}" >&2
echo "Migrate the existing state using docs/remote-state.md before running this task." >&2
exit 1
