from __future__ import annotations

import json
import os
import sys
import time
from dataclasses import dataclass
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import quote
from urllib.request import Request, urlopen

from azure.identity import ManagedIdentityCredential


API_VERSION = "v1"
TOKEN_SCOPE = "https://ai.azure.com/.default"


@dataclass(frozen=True)
class AgentDefinition:
    name: str
    kind: str


class FoundryClient:
    def __init__(
        self,
        endpoint: str,
        client_id: str,
        *,
        attempts: int = 8,
        retry_delay_seconds: int = 10,
    ) -> None:
        self._endpoint = endpoint.rstrip("/")
        self._credential = ManagedIdentityCredential(client_id=client_id)
        self._attempts = attempts
        self._retry_delay_seconds = retry_delay_seconds

    def deploy(
        self,
        agent: AgentDefinition,
        image: str,
        model_deployment: str,
    ) -> str:
        existing_version = self._find_matching_version(agent, image, model_deployment)
        if existing_version is not None:
            version, status = existing_version
            if status == "creating":
                self._wait_until_active(agent.name, version)
                return version

            print(
                f"{agent.name}: version {version} already uses {image}; skipping",
                flush=True,
            )
            return version

        definition = {
            "kind": "hosted",
            "container_configuration": {"image": image},
            "cpu": "0.5",
            "memory": "1Gi",
            "protocol_versions": [{"protocol": "invocations", "version": "2.0.0"}],
            "environment_variables": {
                "BANKING_AGENT_KIND": agent.kind,
                "AZURE_AI_MODEL_DEPLOYMENT_NAME": model_deployment,
            },
        }

        if self._agent_exists(agent.name):
            result = self._request(
                "POST",
                f"/agents/{quote(agent.name, safe='')}/versions",
                {"definition": definition},
            )
        else:
            result = self._request(
                "POST",
                "/agents",
                {"name": agent.name, "definition": definition},
            )

        version = str(result.get("version", "")).strip()
        if not version:
            raise RuntimeError(f"{agent.name}: Foundry did not return an agent version")

        self._wait_until_active(agent.name, version)
        return version

    def _agent_exists(self, name: str) -> bool:
        try:
            self._request("GET", f"/agents/{quote(name, safe='')}")
            return True
        except HTTPError as error:
            if error.code == 404:
                return False
            raise

    def _find_matching_version(
        self,
        agent: AgentDefinition,
        image: str,
        model_deployment: str,
    ) -> tuple[str, str] | None:
        if not self._agent_exists(agent.name):
            return None

        response = self._request("GET", f"/agents/{quote(agent.name, safe='')}/versions")
        versions = _items(response)
        for version in versions:
            status = str(version.get("status", "")).lower()
            if status not in {"active", "creating"}:
                continue

            definition = version.get("definition") or {}
            container = definition.get("container_configuration") or {}
            environment = definition.get("environment_variables") or {}
            if (
                container.get("image") == image
                and environment.get("BANKING_AGENT_KIND") == agent.kind
                and environment.get("AZURE_AI_MODEL_DEPLOYMENT_NAME") == model_deployment
            ):
                return str(version.get("version")), status

        return None

    def _wait_until_active(self, name: str, version: str) -> None:
        for attempt in range(60):
            result = self._request(
                "GET",
                f"/agents/{quote(name, safe='')}/versions/{quote(version, safe='')}",
            )
            status = str(result.get("status", "")).lower()
            print(f"{name}: version {version} status={status}", flush=True)

            if status == "active":
                return
            if status == "failed":
                raise RuntimeError(f"{name}: deployment failed: {result.get('error')}")

            time.sleep(5)

        raise TimeoutError(f"{name}: timed out waiting for version {version} to become active")

    def _request(
        self,
        method: str,
        path: str,
        body: dict[str, Any] | None = None,
    ) -> dict[str, Any]:
        url = f"{self._endpoint}{path}?api-version={API_VERSION}"
        payload = json.dumps(body).encode("utf-8") if body is not None else None

        for attempt in range(1, self._attempts + 1):
            token = self._credential.get_token(TOKEN_SCOPE).token
            request = Request(
                url,
                data=payload,
                method=method,
                headers={
                    "Authorization": f"Bearer {token}",
                    "Content-Type": "application/json",
                    "Accept": "application/json",
                },
            )

            try:
                with urlopen(request, timeout=60) as response:
                    response_body = response.read().decode("utf-8")
                    return json.loads(response_body) if response_body else {}
            except HTTPError as error:
                if error.code == 404:
                    raise

                response_body = error.read().decode("utf-8", errors="replace")
                if error.code not in {401, 403, 408, 409, 429} and error.code < 500:
                    raise RuntimeError(
                        f"Foundry request failed: {method} {path} returned "
                        f"{error.code}: {response_body}"
                    ) from error

                if attempt == self._attempts:
                    raise RuntimeError(
                        f"Foundry request failed after {attempt} attempts: "
                        f"{method} {path} returned {error.code}: {response_body}"
                    ) from error
            except URLError as error:
                if attempt == self._attempts:
                    raise RuntimeError(
                        f"Foundry request failed after {attempt} attempts: {method} {path}: {error}"
                    ) from error

            time.sleep(self._retry_delay_seconds * attempt)

        raise AssertionError("request retry loop exited unexpectedly")


def _items(response: Any) -> list[dict[str, Any]]:
    if isinstance(response, list):
        return response
    if isinstance(response, dict):
        for key in ("value", "data", "items"):
            value = response.get(key)
            if isinstance(value, list):
                return value
    return []


def _definitions(raw: str) -> list[AgentDefinition]:
    parsed = json.loads(raw)
    if not isinstance(parsed, list) or not parsed:
        raise ValueError("AGENT_DEFINITIONS must be a non-empty JSON array")

    definitions = []
    for item in parsed:
        if not isinstance(item, dict) or not item.get("name") or not item.get("kind"):
            raise ValueError("Each agent definition requires non-empty name and kind values")
        definitions.append(AgentDefinition(name=item["name"], kind=item["kind"]))
    return definitions


def main() -> int:
    endpoint = os.environ["FOUNDRY_PROJECT_ENDPOINT"]
    client_id = os.environ["AZURE_CLIENT_ID"]
    image = os.environ["HOSTED_AGENT_IMAGE"]
    model_deployment = os.environ["AZURE_AI_MODEL_DEPLOYMENT_NAME"]
    agents = _definitions(os.environ["AGENT_DEFINITIONS"])

    client = FoundryClient(endpoint, client_id)
    deployed = {}
    for agent in agents:
        deployed[agent.name] = client.deploy(agent, image, model_deployment)

    print(json.dumps({"status": "ok", "agents": deployed}, sort_keys=True), flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"Hosted Agent deployment failed: {error}", file=sys.stderr, flush=True)
        raise
