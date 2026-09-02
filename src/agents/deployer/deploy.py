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
# Memory stores are a preview feature served under their own api-version, which
# is distinct from the (stable) v1 used by the agents API.
MEMORY_API_VERSION = "2025-11-15-preview"
TOKEN_SCOPE = "https://ai.azure.com/.default"
READY_STATUSES = {"active", "running"}
PENDING_STATUSES = {"creating", "starting", "updating"}
PROJECT_ENDPOINT_ENV_VAR = "BANKING_AGENT_PROJECT_ENDPOINT"
# Per-invocation timeout for a hosted agent. The dispute-planning graph runs up
# to four sequential model calls, so the previous 30s default left no headroom.
INVOKE_TIMEOUT_ENV_VAR = "BANKING_AGENT_INVOKE_TIMEOUT_SECONDS"
INVOKE_TIMEOUT_SECONDS = os.environ.get(INVOKE_TIMEOUT_ENV_VAR, "90")
# Name of the shared toolbox that hosted agents consume over MCP at runtime.
# Empty means the feature is off and the agents run exactly as before.
HOSTED_TOOLBOX_ENV_VAR = "BANKING_AGENT_TOOLBOX_NAME"
HOSTED_TOOLBOX_NAME = os.environ.get("TOOLBOX_NAME", "").strip()
LEGACY_PROJECT_ENDPOINT_ENV_VAR = "FOUNDRY_PROJECT_ENDPOINT"


@dataclass(frozen=True)
class AgentDefinition:
    name: str
    kind: str


@dataclass(frozen=True)
class ToolboxDefinition:
    """A Foundry toolbox: several managed tools behind one MCP endpoint.

    The toolbox exists for *hosted* agents, because a hosted agent definition
    has no declarative `tools` array. The container calls this endpoint at
    runtime and authenticates with its own agent identity.

    It is deliberately not exposed as an `mcp` tool for prompt agents. Foundry
    has no way to bind a prompt agent's `mcp` tool to the agent identity -- the
    tool's `authorization` field is a literal header string -- so the call is
    rejected with a 401 at invocation time. Prompt agents declare managed tools
    inline instead; see `_memory_agent_tools`.
    """

    name: str
    tools: list[dict[str, Any]]
    description: str = "Banking agent shared tools"


@dataclass(frozen=True)
class MemoryAgentDefinition:
    """A Foundry `prompt` agent with the managed memory search tool attached.

    Unlike the hosted container agents, Foundry itself runs the model loop and
    the memory tool for this agent, so there is no image to build.
    """

    agent_name: str
    memory_store_name: str
    instructions: str
    user_profile_details: str
    scope: str = "{{$userId}}"
    update_delay_seconds: int = 300
    ttl_seconds: int = 2592000

    def tool(self) -> dict[str, Any]:
        return {
            "type": "memory_search_preview",
            "memory_store_name": self.memory_store_name,
            "scope": self.scope,
            "update_delay": self.update_delay_seconds,
        }

    def definition(
        self,
        model_deployment: str,
        extra_tools: list[dict[str, Any]] | None = None,
    ) -> dict[str, Any]:
        return {
            "kind": "prompt",
            "model": model_deployment,
            "instructions": self.instructions,
            "tools": [self.tool(), *(extra_tools or [])],
        }

    def store_body(self, chat_model: str, embedding_model: str) -> dict[str, Any]:
        return {
            "name": self.memory_store_name,
            "description": "Banking assistant customer memory",
            "definition": {
                "kind": "default",
                "chat_model": chat_model,
                "embedding_model": embedding_model,
                "options": {
                    "chat_summary_enabled": True,
                    "user_profile_enabled": True,
                    # Memory extraction is model-driven, so the only durable
                    # control over what lands in the store is this instruction.
                    # Banking chat is dense with exactly the data we must not
                    # retain, so it is set deliberately rather than defaulted.
                    "user_profile_details": self.user_profile_details,
                    "default_ttl_seconds": self.ttl_seconds,
                },
            },
        }


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
        project_endpoint: str,
    ) -> str:
        existing_version = self._find_matching_version(
            agent, image, model_deployment, project_endpoint
        )
        if existing_version is not None:
            version, status = existing_version
            if status in PENDING_STATUSES:
                self._wait_until_running(agent.name, version)
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
                # Explicit runtime configuration — every env var that affects
                # runtime behaviour participates in version/redeploy matching so
                # that a config change always creates or updates a version.
                PROJECT_ENDPOINT_ENV_VAR: project_endpoint,
                "ALLOW_FALLBACK": "false",
                # Multi-node graphs issue one model call per node, so the
                # per-invocation budget must cover the longest path, not a
                # single call. Foundry rejects AGENT_* and FOUNDRY_* names as
                # reserved, hence the BANKING_ prefix.
                INVOKE_TIMEOUT_ENV_VAR: INVOKE_TIMEOUT_SECONDS,
            },
        }

        if HOSTED_TOOLBOX_NAME:
            definition["environment_variables"][HOSTED_TOOLBOX_ENV_VAR] = HOSTED_TOOLBOX_NAME

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

        version = _version(result)
        status = str(result.get("status", "")).lower()
        if not version:
            version, status = self._wait_for_created_version(
                agent,
                image,
                model_deployment,
                project_endpoint,
            )

        if status not in READY_STATUSES:
            self._wait_until_running(agent.name, version)
        return version

    def ensure_toolbox(self, toolbox: ToolboxDefinition) -> str:
        """Create a toolbox version, reusing the default version if it matches.

        Toolbox versions are immutable, so an unchanged tool set must not
        create a new version on every deploy.
        """
        existing = self._matching_toolbox_version(toolbox)
        if existing is not None:
            print(f"{toolbox.name}: toolbox version {existing} already matches; skipping", flush=True)
            return existing

        result = self._request(
            "POST",
            f"/toolboxes/{quote(toolbox.name, safe='')}/versions",
            {"description": toolbox.description, "tools": toolbox.tools},
        )
        version = str(result.get("version", "")).strip()
        print(f"{toolbox.name}: created toolbox version {version or '(unknown)'}", flush=True)
        return version

    def _matching_toolbox_version(self, toolbox: ToolboxDefinition) -> str | None:
        try:
            response = self._request(
                "GET", f"/toolboxes/{quote(toolbox.name, safe='')}/versions"
            )
        except HTTPError as error:
            if error.code == 404:
                return None
            raise

        for version in _items(response):
            if _tool_types(version.get("tools")) == _tool_types(toolbox.tools):
                return str(version.get("version"))

        return None

    def ensure_memory_store(
        self,
        agent: MemoryAgentDefinition,
        chat_model: str,
        embedding_model: str,
    ) -> None:
        path = f"/memory_stores/{quote(agent.memory_store_name, safe='')}"
        try:
            self._request("GET", path, api_version=MEMORY_API_VERSION)
            print(
                f"{agent.memory_store_name}: memory store already exists; skipping create",
                flush=True,
            )
            return
        except HTTPError as error:
            if error.code != 404:
                raise

        self._request(
            "POST",
            "/memory_stores",
            agent.store_body(chat_model, embedding_model),
            api_version=MEMORY_API_VERSION,
        )
        print(f"{agent.memory_store_name}: created memory store", flush=True)

    def deploy_prompt_agent(
        self,
        agent: MemoryAgentDefinition,
        model_deployment: str,
        extra_tools: list[dict[str, Any]] | None = None,
    ) -> str:
        definition = agent.definition(model_deployment, extra_tools)

        existing_version = self._find_matching_prompt_version(agent, definition)
        if existing_version is not None:
            version, status = existing_version
            if status in PENDING_STATUSES:
                self._wait_until_running(agent.agent_name, version)
                return version

            print(
                f"{agent.agent_name}: version {version} already matches definition; skipping",
                flush=True,
            )
            return version

        if self._agent_exists(agent.agent_name):
            result = self._request(
                "POST",
                f"/agents/{quote(agent.agent_name, safe='')}/versions",
                {"definition": definition},
            )
        else:
            result = self._request(
                "POST",
                "/agents",
                {"name": agent.agent_name, "definition": definition},
            )

        version = _version(result)
        if not version:
            raise RuntimeError(f"{agent.agent_name}: Foundry did not return a created version")

        status = str(result.get("status", "")).lower()
        if status not in READY_STATUSES:
            self._wait_until_running(agent.agent_name, version)
        return version

    def _find_matching_prompt_version(
        self,
        agent: MemoryAgentDefinition,
        definition: dict[str, Any],
    ) -> tuple[str, str] | None:
        if not self._agent_exists(agent.agent_name):
            return None

        response = self._request("GET", f"/agents/{quote(agent.agent_name, safe='')}/versions")
        for version in _items(response):
            status = str(version.get("status", "")).lower()
            if status not in READY_STATUSES | PENDING_STATUSES:
                continue

            existing = version.get("definition") or {}
            if (
                existing.get("kind") == definition["kind"]
                and existing.get("model") == definition["model"]
                and existing.get("instructions") == definition["instructions"]
                and _tools(existing) == definition["tools"]
            ):
                return str(version.get("version")), status

        return None

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
        project_endpoint: str,
    ) -> tuple[str, str] | None:
        if not self._agent_exists(agent.name):
            return None

        response = self._request("GET", f"/agents/{quote(agent.name, safe='')}/versions")
        versions = _items(response)
        for version in versions:
            status = str(version.get("status", "")).lower()
            if status not in READY_STATUSES | PENDING_STATUSES:
                continue

            definition = version.get("definition") or {}
            container = definition.get("container_configuration") or {}
            environment = definition.get("environment_variables") or {}
            runtime_project_endpoint = environment.get(PROJECT_ENDPOINT_ENV_VAR) or environment.get(
                LEGACY_PROJECT_ENDPOINT_ENV_VAR
            )
            if (
                container.get("image") == image
                and environment.get("BANKING_AGENT_KIND") == agent.kind
                and environment.get("AZURE_AI_MODEL_DEPLOYMENT_NAME") == model_deployment
                and runtime_project_endpoint == project_endpoint
                and environment.get("ALLOW_FALLBACK") == "false"
                and environment.get(INVOKE_TIMEOUT_ENV_VAR) == INVOKE_TIMEOUT_SECONDS
                and environment.get(HOSTED_TOOLBOX_ENV_VAR, "") == HOSTED_TOOLBOX_NAME
            ):
                return str(version.get("version")), status

        return None

    def _wait_for_created_version(
        self,
        agent: AgentDefinition,
        image: str,
        model_deployment: str,
        project_endpoint: str,
    ) -> tuple[str, str]:
        for _ in range(30):
            existing_version = self._find_matching_version(
                agent,
                image,
                model_deployment,
                project_endpoint,
            )
            if existing_version is not None:
                return existing_version

            time.sleep(5)

        raise TimeoutError(
            f"{agent.name}: timed out waiting for Foundry to return the created version"
        )

    def _wait_until_running(self, name: str, version: str) -> None:
        for attempt in range(60):
            result = self._request(
                "GET",
                f"/agents/{quote(name, safe='')}/versions/{quote(version, safe='')}",
            )
            status = str(result.get("status", "")).lower()
            print(f"{name}: version {version} status={status}", flush=True)

            if status in READY_STATUSES:
                return
            if status == "failed":
                raise RuntimeError(f"{name}: deployment failed: {result.get('error')}")

            time.sleep(5)

        raise TimeoutError(f"{name}: timed out waiting for version {version} to start running")

    def _request(
        self,
        method: str,
        path: str,
        body: dict[str, Any] | None = None,
        *,
        api_version: str = API_VERSION,
    ) -> dict[str, Any]:
        url = f"{self._endpoint}{path}?api-version={api_version}"
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


TOOL_COMPARISON_KEYS = (
    "type",
    "memory_store_name",
    "scope",
    "update_delay",
    "server_label",
    "server_url",
    "require_approval",
)


def _tool_types(tools: Any) -> list[str]:
    """The ordered tool identity of a toolbox version.

    Compared instead of the whole payload because Foundry returns
    server-populated fields that we never sent.
    """
    if not isinstance(tools, list):
        return []
    return sorted(
        f"{tool.get('type')}:{tool.get('name') or tool.get('server_label') or ''}"
        for tool in tools
        if isinstance(tool, dict)
    )


def _tools(definition: dict[str, Any]) -> list[dict[str, Any]]:
    """Project returned tools onto the keys we set.

    Foundry echoes back server-side defaults alongside the fields we submit, so
    comparing the raw payloads would report a difference on every run and
    create a new agent version each deploy.
    """

    tools = definition.get("tools")
    if not isinstance(tools, list):
        return []

    return [
        {key: tool.get(key) for key in TOOL_COMPARISON_KEYS if key in tool}
        for tool in tools
        if isinstance(tool, dict)
    ]


def _items(response: Any) -> list[dict[str, Any]]:
    if isinstance(response, list):
        return response
    if isinstance(response, dict):
        for key in ("value", "data", "items"):
            value = response.get(key)
            if isinstance(value, list):
                return value
    return []


def _version(response: dict[str, Any]) -> str:
    version = str(response.get("version", "")).strip()
    if version:
        return version

    identifier = str(response.get("id", "")).strip()
    if ":" in identifier:
        return identifier.rsplit(":", 1)[1]

    return ""


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


def _project_endpoint() -> str:
    endpoint = os.getenv(PROJECT_ENDPOINT_ENV_VAR) or os.getenv(LEGACY_PROJECT_ENDPOINT_ENV_VAR)
    if not endpoint:
        raise KeyError(
            f"Missing Foundry project endpoint. Set {PROJECT_ENDPOINT_ENV_VAR} or "
            f"{LEGACY_PROJECT_ENDPOINT_ENV_VAR}."
        )
    return endpoint


def _toolbox() -> ToolboxDefinition | None:
    """Build the shared toolbox from configuration, or None when disabled."""
    name = os.getenv("TOOLBOX_NAME", "").strip()
    if not name:
        return None

    raw = os.getenv("TOOLBOX_TOOLS", "").strip()
    if not raw:
        raise KeyError("TOOLBOX_TOOLS must be set when TOOLBOX_NAME is configured")

    tools = json.loads(raw)
    if not isinstance(tools, list) or not tools:
        raise ValueError("TOOLBOX_TOOLS must be a non-empty JSON array")
    for tool in tools:
        if not isinstance(tool, dict) or not tool.get("type"):
            raise ValueError("Each toolbox tool requires a non-empty type")

    _assert_tool_identifiers(tools)

    return ToolboxDefinition(name=name, tools=tools)


def _assert_tool_identifiers(tools: list) -> None:
    """Reject tool sets Foundry will refuse when the toolbox version is created.

    Foundry allows at most one tool without an identifier, and identifiers must
    be unique. Catching this here fails the deploy with the offending tool types
    named, instead of a 400 buried in a urllib traceback.
    """
    identifiers = [
        str(tool.get("name") or tool.get("server_label") or "").strip() for tool in tools
    ]

    unidentified = [
        str(tool.get("type"))
        for tool, identifier in zip(tools, identifiers)
        if not identifier
    ]
    if len(unidentified) > 1:
        raise ValueError(
            "Foundry allows at most one toolbox tool without a 'name' or "
            "'server_label'. Add an identifier to these tool types: "
            + ", ".join(sorted(unidentified))
        )

    named = [identifier for identifier in identifiers if identifier]
    duplicates = sorted({name for name in named if named.count(name) > 1})
    if duplicates:
        raise ValueError(
            "Toolbox tool identifiers must be unique. Duplicated: "
            + ", ".join(duplicates)
        )


def _memory_agent_tools() -> list[dict[str, Any]]:
    """Declarative tools attached directly to the memory prompt agent.

    A prompt agent must NOT reach its tools through the project toolbox. The
    toolbox MCP endpoint only accepts a caller-supplied credential, and the
    `mcp` tool's `authorization` field is a literal header string rather than a
    reference to the agent identity. Pointing a prompt agent at the toolbox
    therefore fails at invocation time with a 401 from the MCP endpoint, which
    surfaces as `tool_user_error` and takes the whole response down -- including
    the memory tool that would otherwise have worked.

    Prompt agents do not need the indirection: Foundry runs their tool loop, so
    managed tools can be declared inline. The toolbox stays in place for the
    hosted container agents, which have no declarative tools array and do
    authenticate to it with their own identity from inside the container.
    """

    raw = os.getenv("MEMORY_AGENT_TOOLS", "").strip()
    if not raw:
        return []

    tools = json.loads(raw)
    if not isinstance(tools, list):
        raise ValueError("MEMORY_AGENT_TOOLS must be a JSON array")

    for tool in tools:
        if not isinstance(tool, dict) or not str(tool.get("type", "")).strip():
            raise ValueError("Each MEMORY_AGENT_TOOLS entry requires a non-empty type")
        if tool.get("type") == "mcp":
            raise ValueError(
                "MEMORY_AGENT_TOOLS must not contain an 'mcp' tool. Foundry cannot "
                "authenticate a prompt agent to the project toolbox with its agent "
                "identity, so the tool fails at invocation time. Declare the managed "
                "tool inline instead."
            )

    _assert_tool_identifiers(tools)
    return tools


def _memory_agent() -> MemoryAgentDefinition | None:
    """Build the memory-backed prompt agent from configuration.

    Returns None when the feature is not configured, so an environment without
    an embedding model deployment continues to deploy the hosted agents.
    """

    store_name = os.getenv("MEMORY_STORE_NAME", "").strip()
    if not store_name:
        return None

    user_profile_details = os.getenv("MEMORY_USER_PROFILE_DETAILS", "").strip()
    if not user_profile_details:
        raise KeyError(
            "MEMORY_USER_PROFILE_DETAILS must be set when MEMORY_STORE_NAME is configured. "
            "Memory extraction is model-driven, so the redaction instruction is required."
        )

    instructions = os.getenv("MEMORY_AGENT_INSTRUCTIONS", "").strip()
    if not instructions:
        raise KeyError("MEMORY_AGENT_INSTRUCTIONS must be set when MEMORY_STORE_NAME is configured")

    return MemoryAgentDefinition(
        agent_name=os.getenv("MEMORY_AGENT_NAME", "customer-profile").strip(),
        memory_store_name=store_name,
        instructions=instructions,
        user_profile_details=user_profile_details,
        update_delay_seconds=int(os.getenv("MEMORY_UPDATE_DELAY_SECONDS", "300")),
        ttl_seconds=int(os.getenv("MEMORY_TTL_SECONDS", "2592000")),
    )


def main() -> int:
    endpoint = _project_endpoint()
    client_id = os.environ["AZURE_CLIENT_ID"]
    image = os.environ["HOSTED_AGENT_IMAGE"]
    model_deployment = os.environ["AZURE_AI_MODEL_DEPLOYMENT_NAME"]
    agents = _definitions(os.environ["AGENT_DEFINITIONS"])

    client = FoundryClient(endpoint, client_id)
    deployed = {}
    for agent in agents:
        deployed[agent.name] = client.deploy(agent, image, model_deployment, endpoint)

    # The toolbox serves the hosted container agents, which authenticate to it
    # with their own identity at runtime. It is deliberately not attached to the
    # prompt agent below; see _memory_agent_tools for why.
    toolbox = _toolbox()
    if toolbox is not None:
        client.ensure_toolbox(toolbox)

    memory_agent = _memory_agent()
    if memory_agent is not None:
        embedding_deployment = os.environ["AZURE_AI_EMBEDDING_DEPLOYMENT_NAME"]
        client.ensure_memory_store(memory_agent, model_deployment, embedding_deployment)
        deployed[memory_agent.agent_name] = client.deploy_prompt_agent(
            memory_agent, model_deployment, extra_tools=_memory_agent_tools()
        )

    print(json.dumps({"status": "ok", "agents": deployed}, sort_keys=True), flush=True)
    return 0


if __name__ == "__main__":
    try:
        sys.exit(main())
    except Exception as error:
        print(f"Hosted Agent deployment failed: {error}", file=sys.stderr, flush=True)
        raise
