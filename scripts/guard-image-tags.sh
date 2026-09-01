#!/usr/bin/env bash
# Fails fast when the images the apps stack is about to deploy are not in ACR.
#
# `task app:build` and `task app:apply` each derive the image tag independently
# from `git rev-parse HEAD`. Any commit between the two steps - including a
# docs-only commit - moves the tag that apply requests without rebuilding the
# images, so ARM rejects the container definition with:
#
#   InvalidParameterValueInContainerTemplate: ... 'Invalid value:
#   "<acr>/agent-deployer:<tag>": MANIFEST_UNKNOWN: manifest tagged by "<tag>"
#   is not found'
#
# That 400 arrives midway through the apply, after some resources have already
# been created, which leaves the stack half-applied and the next run replacing
# tainted resources. Checking the tags up front turns it into an actionable
# message before anything changes.
#
# The repository list mirrors the images referenced in apps/main.tf.

set -euo pipefail

readonly REPOSITORIES=(
  orchestrator
  webui
  hosted-agents
  agent-deployer
  database-migrator
)

image_tag="${1:-}"

if [[ -z "${image_tag}" ]]; then
  echo "ERROR: An image tag is required." >&2
  exit 1
fi

if ! acr_name="$(terraform -chdir=./infrastructure output -raw ACR_NAME 2>/dev/null)" \
  || [[ -z "${acr_name}" ]]; then
  echo "ERROR: Could not read ACR_NAME from the infrastructure stack." >&2
  echo "The infrastructure stack has not been applied. Run 'task cloud:up' first." >&2
  exit 1
fi

missing=()

for repository in "${REPOSITORIES[@]}"; do
  if ! az acr repository show \
    --name "${acr_name}" \
    --image "${repository}:${image_tag}" \
    --output none 2>/dev/null; then
    missing+=("${repository}:${image_tag}")
  fi
done

if (( ${#missing[@]} == 0 )); then
  exit 0
fi

echo "ERROR: The following images are not present in ${acr_name}:" >&2
printf '  %s\n' "${missing[@]}" >&2
echo "" >&2
echo "The apps stack deploys images tagged with the current commit (${image_tag})." >&2
echo "Commits made after the last 'task app:build' move that tag, so the images" >&2
echo "must be rebuilt before applying:" >&2
echo "" >&2
echo "  task app:build" >&2
echo "" >&2
echo "To deploy a tag that already exists instead, pass it explicitly:" >&2
echo "" >&2
echo "  terraform -chdir=apps apply -var \"image_tag=<existing-tag>\" ..." >&2
exit 1
