"""Shared Azure log-query helpers for the repository's operational scripts.

The workspace a Container App writes to is not guessable from the stack name.
It is a property of the Container Apps *environment*, so it has to be resolved
through the environment rather than assumed from a naming convention -- a
convention lookup keeps working right up until someone renames a stack, and
then fails in a way that looks like "no logs" rather than "wrong workspace".

This module exists so that `smoke-mvp.py` and `cloud-logs.py` resolve it the
same way. When one of them is wrong, both are wrong, which is much easier to
notice than one of them quietly reading an empty workspace.
"""
from __future__ import annotations

import json
import subprocess
from typing import Any


class AzureQueryError(RuntimeError):
    """An `az` invocation failed, carrying whatever the CLI said about it."""


def _run(args: list[str]) -> str:
    try:
        completed = subprocess.run(args, check=True, capture_output=True, text=True)
    except FileNotFoundError as error:
        raise AzureQueryError("The Azure CLI ('az') is not on PATH.") from error
    except subprocess.CalledProcessError as error:
        detail = (error.stderr or "").strip() or (error.stdout or "").strip()
        raise AzureQueryError(detail or f"'{' '.join(args[:3])}' failed.") from error
    return completed.stdout.strip()


def list_container_apps(resource_group: str) -> list[str]:
    output = _run(
        [
            "az",
            "containerapp",
            "list",
            "--resource-group",
            resource_group,
            "--query",
            "[].name",
            "--output",
            "json",
        ]
    )
    return json.loads(output or "[]")


def resolve_container_app(resource_group: str, requested: str) -> str:
    """Map a short name like ``orchestrator`` to its deployed app name.

    Deployed names carry a generated stack prefix (``whippet-59851-``), which
    nobody wants to type and which changes every time the stack is rebuilt.
    Matching on the suffix keeps the short name stable across deployments.
    """
    names = list_container_apps(resource_group)
    if not names:
        raise AzureQueryError(
            f"No Container Apps found in resource group '{resource_group}'. "
            "Is the stack deployed, and is the Azure CLI pointed at the right subscription?"
        )

    if requested in names:
        return requested

    matches = [name for name in names if name.endswith(f"-{requested}")]
    if len(matches) == 1:
        return matches[0]

    available = ", ".join(sorted(names))
    if not matches:
        raise AzureQueryError(
            f"No Container App matching '{requested}'. Available: {available}"
        )
    raise AzureQueryError(
        f"'{requested}' matches more than one Container App ({', '.join(sorted(matches))}). "
        "Use the full name."
    )


def resolve_workspace_id(resource_group: str, app_name: str) -> str:
    """The Log Analytics customer ID behind a Container App's environment."""
    environment_id = _run(
        [
            "az",
            "containerapp",
            "show",
            "--name",
            app_name,
            "--resource-group",
            resource_group,
            "--query",
            "properties.environmentId",
            "--output",
            "tsv",
        ]
    )
    if not environment_id:
        raise AzureQueryError(f"Container App '{app_name}' reported no environment.")

    workspace_id = _run(
        [
            "az",
            "resource",
            "show",
            "--ids",
            environment_id,
            "--query",
            "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId",
            "--output",
            "tsv",
        ]
    )
    if not workspace_id:
        raise AzureQueryError(
            "The Container Apps environment has no Log Analytics workspace configured, "
            "so there is nowhere to read logs from."
        )
    return workspace_id


def run_kql(workspace_id: str, query: str) -> list[dict[str, Any]]:
    output = _run(
        [
            "az",
            "monitor",
            "log-analytics",
            "query",
            "--workspace",
            workspace_id,
            "--analytics-query",
            query,
            "--output",
            "json",
        ]
    )
    try:
        return json.loads(output or "[]")
    except json.JSONDecodeError as error:
        raise AzureQueryError(f"Log Analytics returned output that is not JSON: {output[:200]}") from error
