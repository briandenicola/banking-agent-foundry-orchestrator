import json
import os
import unittest
from unittest.mock import Mock, patch
from urllib.error import HTTPError

from deploy import (
    INVOKE_TIMEOUT_ENV_VAR,
    INVOKE_TIMEOUT_SECONDS,
    MEMORY_API_VERSION,
    AgentDefinition,
    FoundryClient,
    MemoryAgentDefinition,
    ToolboxDefinition,
    _definitions,
    _items,
    _memory_agent,
    _toolbox,
    _project_endpoint,
    _version,
)

_IMAGE = "example.azurecr.io/hosted-agents:abc123"
_MODEL = "gpt-5.4-mini"
_ENDPOINT = "https://foundry.example.test/project"
_AGENT = AgentDefinition("workflow-planning", "workflow-planning")


class DeployerContractTests(unittest.TestCase):
    def test_definitions_parse_all_agents(self):
        agents = _definitions(
            json.dumps(
                [
                    {"name": "workflow-planning", "kind": "workflow-planning"},
                    {"name": "dispute-planning", "kind": "dispute-planning"},
                ]
            )
        )

        self.assertEqual(
            [
                AgentDefinition("workflow-planning", "workflow-planning"),
                AgentDefinition("dispute-planning", "dispute-planning"),
            ],
            agents,
        )

    def test_definitions_reject_empty_input(self):
        with self.assertRaises(ValueError):
            _definitions("[]")

    def test_items_accepts_foundry_collection_shapes(self):
        expected = [{"version": "1"}]
        self.assertEqual(expected, _items(expected))
        self.assertEqual(expected, _items({"value": expected}))
        self.assertEqual(expected, _items({"data": expected}))

    def _make_version_response(
        self,
        image,
        kind,
        model,
        endpoint,
        fallback="false",
        status="creating",
        timeout=INVOKE_TIMEOUT_SECONDS,
    ):
        return {
            "value": [
                {
                    "version": "2",
                    "status": status,
                    "definition": {
                        "container_configuration": {"image": image},
                        "environment_variables": {
                            "BANKING_AGENT_KIND": kind,
                            "AZURE_AI_MODEL_DEPLOYMENT_NAME": model,
                            "BANKING_AGENT_PROJECT_ENDPOINT": endpoint,
                            "ALLOW_FALLBACK": fallback,
                            INVOKE_TIMEOUT_ENV_VAR: timeout,
                        },
                    },
                }
            ]
        }

    def test_matching_version_reuses_creating_deployment(self):
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value=self._make_version_response(_IMAGE, "workflow-planning", _MODEL, _ENDPOINT)
        )

        result = client._find_matching_version(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        self.assertEqual(("2", "creating"), result)

    def test_matching_version_reuses_running_deployment(self):
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value=self._make_version_response(_IMAGE, "workflow-planning", _MODEL, _ENDPOINT, status="running")
        )

        result = client._find_matching_version(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        self.assertEqual(("2", "running"), result)

    def test_matching_version_misses_on_different_endpoint(self):
        """A version with a different BANKING_AGENT_PROJECT_ENDPOINT must not match."""
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value=self._make_version_response(
                _IMAGE, "workflow-planning", _MODEL, "https://other.endpoint/project"
            )
        )

        result = client._find_matching_version(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        self.assertIsNone(result)

    def test_matching_version_misses_when_fallback_not_disabled(self):
        """A version without ALLOW_FALLBACK=false must not match."""
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value=self._make_version_response(
                _IMAGE, "workflow-planning", _MODEL, _ENDPOINT, fallback="true"
            )
        )

        result = client._find_matching_version(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        self.assertIsNone(result)

    def test_matching_version_misses_when_invoke_timeout_differs(self):
        """A version deployed with a different invocation timeout must not match.

        Multi-node graphs made this budget behaviour-affecting, so a change to
        it has to force a new agent version rather than silently reuse an old
        one still running the previous timeout.
        """
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value=self._make_version_response(
                _IMAGE, "workflow-planning", _MODEL, _ENDPOINT, timeout="30"
            )
        )

        result = client._find_matching_version(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        self.assertIsNone(result)

    def test_deploy_discovers_version_when_create_response_omits_it(self):
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._find_matching_version = Mock(
            side_effect=[None, ("1", "active")]
        )
        client._agent_exists = Mock(return_value=False)
        client._request = Mock(return_value={"object": "agent.version"})

        version = client.deploy(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        self.assertEqual("1", version)
        definition = client._request.call_args.args[2]["definition"]
        env = definition["environment_variables"]
        self.assertEqual(_ENDPOINT, env["BANKING_AGENT_PROJECT_ENDPOINT"])
        self.assertEqual("false", env["ALLOW_FALLBACK"])
        self.assertEqual(INVOKE_TIMEOUT_SECONDS, env[INVOKE_TIMEOUT_ENV_VAR])

    def test_no_environment_variable_uses_a_foundry_reserved_prefix(self):
        """Foundry rejects AGENT_* and FOUNDRY_* names as reserved for platform use.

        This is a deploy-time 400 that no amount of local testing catches, so
        the naming rule is asserted here instead.
        """
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._find_matching_version = Mock(return_value=None)
        client._agent_exists = Mock(return_value=False)
        client._request = Mock(return_value={"version": "5", "status": "active"})

        client.deploy(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        env = client._request.call_args.args[2]["definition"]["environment_variables"]
        reserved = [
            name
            for name in env
            if name.startswith("AGENT_") or name.startswith("FOUNDRY_")
        ]
        self.assertEqual([], reserved)

    def test_deploy_definition_includes_required_env_vars(self):
        """Every runtime-affecting env var must be present in the deployed definition."""
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._find_matching_version = Mock(return_value=None)
        client._agent_exists = Mock(return_value=False)
        client._request = Mock(return_value={"version": "5", "status": "active"})

        client.deploy(_AGENT, _IMAGE, _MODEL, _ENDPOINT)

        definition = client._request.call_args.args[2]["definition"]
        env = definition["environment_variables"]
        self.assertIn("BANKING_AGENT_KIND", env)
        self.assertIn("AZURE_AI_MODEL_DEPLOYMENT_NAME", env)
        self.assertIn("BANKING_AGENT_PROJECT_ENDPOINT", env)
        self.assertIn("ALLOW_FALLBACK", env)
        self.assertEqual("false", env["ALLOW_FALLBACK"])

    def test_project_endpoint_prefers_non_reserved_env_var(self):
        with patch.dict(
            os.environ,
            {"BANKING_AGENT_PROJECT_ENDPOINT": _ENDPOINT, "FOUNDRY_PROJECT_ENDPOINT": "https://legacy.example.test/project"},
            clear=True,
        ):
            self.assertEqual(_ENDPOINT, _project_endpoint())

    def test_project_endpoint_falls_back_to_legacy_env_var(self):
        with patch.dict(
            os.environ,
            {"FOUNDRY_PROJECT_ENDPOINT": _ENDPOINT},
            clear=True,
        ):
            self.assertEqual(_ENDPOINT, _project_endpoint())

    def test_version_falls_back_to_agent_version_id(self):
        self.assertEqual("4", _version({"id": "workflow-planning:4"}))


_MEMORY_ENV = {
    "MEMORY_STORE_NAME": "customer_profile_memory",
    "MEMORY_AGENT_NAME": "customer-profile",
    "MEMORY_USER_PROFILE_DETAILS": "Never retain account numbers or balances.",
    "MEMORY_AGENT_INSTRUCTIONS": "You are a retail banking servicing assistant.",
}


def _memory_agent_fixture(**overrides):
    defaults = {
        "agent_name": "customer-profile",
        "memory_store_name": "customer_profile_memory",
        "instructions": "You are a retail banking servicing assistant.",
        "user_profile_details": "Never retain account numbers or balances.",
    }
    defaults.update(overrides)
    return MemoryAgentDefinition(**defaults)


class MemoryAgentConfigTests(unittest.TestCase):
    def test_memory_agent_is_optional(self):
        """Without MEMORY_STORE_NAME the hosted agents must still deploy."""
        with patch.dict(os.environ, {}, clear=True):
            self.assertIsNone(_memory_agent())

    def test_memory_agent_requires_redaction_instruction(self):
        """Memory extraction is model-driven, so the redaction rule is mandatory.

        Falling back to a permissive default here is how banking PII would end
        up in a preview-tier store, so an unset value must fail the deploy.
        """
        env = dict(_MEMORY_ENV)
        del env["MEMORY_USER_PROFILE_DETAILS"]
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(KeyError):
                _memory_agent()

    def test_memory_agent_rejects_blank_redaction_instruction(self):
        env = dict(_MEMORY_ENV, MEMORY_USER_PROFILE_DETAILS="   ")
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(KeyError):
                _memory_agent()

    def test_memory_agent_requires_instructions(self):
        env = dict(_MEMORY_ENV)
        del env["MEMORY_AGENT_INSTRUCTIONS"]
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(KeyError):
                _memory_agent()

    def test_memory_agent_defaults_to_per_user_scope(self):
        """Scope must partition memory per end user.

        A static scope would pool every customer's memories into one
        collection, which is cross-customer contamination in a banking app.
        """
        with patch.dict(os.environ, _MEMORY_ENV, clear=True):
            agent = _memory_agent()

        self.assertEqual("{{$userId}}", agent.scope)
        self.assertEqual("{{$userId}}", agent.tool()["scope"])


class PromptAgentDefinitionTests(unittest.TestCase):
    def test_definition_is_a_prompt_agent_with_the_memory_tool(self):
        definition = _memory_agent_fixture().definition(_MODEL)

        self.assertEqual("prompt", definition["kind"])
        self.assertEqual(_MODEL, definition["model"])
        self.assertEqual(
            [
                {
                    "type": "memory_search_preview",
                    "memory_store_name": "customer_profile_memory",
                    "scope": "{{$userId}}",
                    "update_delay": 300,
                }
            ],
            definition["tools"],
        )

    def test_definition_has_no_container_configuration(self):
        """A prompt agent is model-hosted; sending container fields would fail."""
        definition = _memory_agent_fixture().definition(_MODEL)

        self.assertNotIn("container_configuration", definition)
        self.assertNotIn("environment_variables", definition)

    def test_store_body_carries_the_redaction_instruction(self):
        body = _memory_agent_fixture().store_body(_MODEL, "text-embedding-3-small")
        options = body["definition"]["options"]

        self.assertEqual("default", body["definition"]["kind"])
        self.assertEqual(_MODEL, body["definition"]["chat_model"])
        self.assertEqual("text-embedding-3-small", body["definition"]["embedding_model"])
        self.assertEqual("Never retain account numbers or balances.", options["user_profile_details"])
        self.assertTrue(options["user_profile_enabled"])
        self.assertEqual(2592000, options["default_ttl_seconds"])


class PromptVersionMatchingTests(unittest.TestCase):
    def _client(self, existing_definition, status="active"):
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value={"value": [{"version": "3", "status": status, "definition": existing_definition}]}
        )
        return client

    def test_identical_definition_is_reused(self):
        agent = _memory_agent_fixture()
        definition = agent.definition(_MODEL)
        client = self._client(definition)

        self.assertEqual(("3", "active"), client._find_matching_prompt_version(agent, definition))

    def test_server_added_tool_fields_do_not_force_a_new_version(self):
        """Foundry echoes back defaults; those must not look like a change."""
        agent = _memory_agent_fixture()
        definition = agent.definition(_MODEL)
        echoed = json.loads(json.dumps(definition))
        echoed["tools"][0]["some_server_default"] = "value"
        client = self._client(echoed)

        self.assertEqual(("3", "active"), client._find_matching_prompt_version(agent, definition))

    def test_changed_instructions_force_a_new_version(self):
        agent = _memory_agent_fixture()
        definition = agent.definition(_MODEL)
        stale = json.loads(json.dumps(definition))
        stale["instructions"] = "Older instructions"
        client = self._client(stale)

        self.assertIsNone(client._find_matching_prompt_version(agent, definition))

    def test_changed_scope_forces_a_new_version(self):
        """Tightening scope must not silently keep serving the old version."""
        agent = _memory_agent_fixture()
        definition = agent.definition(_MODEL)
        stale = json.loads(json.dumps(definition))
        stale["tools"][0]["scope"] = "everyone"
        client = self._client(stale)

        self.assertIsNone(client._find_matching_prompt_version(agent, definition))

    def test_missing_tool_forces_a_new_version(self):
        agent = _memory_agent_fixture()
        definition = agent.definition(_MODEL)
        stale = json.loads(json.dumps(definition))
        stale["tools"] = []
        client = self._client(stale)

        self.assertIsNone(client._find_matching_prompt_version(agent, definition))


class MemoryStoreProvisioningTests(unittest.TestCase):
    def _client(self):
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = "https://example.test/project"
        return client

    def test_existing_store_is_not_recreated(self):
        client = self._client()
        client._request = Mock(return_value={"name": "customer_profile_memory"})

        client.ensure_memory_store(_memory_agent_fixture(), _MODEL, "text-embedding-3-small")

        self.assertEqual(1, client._request.call_count)
        self.assertEqual("GET", client._request.call_args.args[0])

    def test_missing_store_is_created_on_the_preview_api_version(self):
        client = self._client()
        not_found = HTTPError("https://example.test", 404, "Not Found", {}, None)
        client._request = Mock(side_effect=[not_found, {"name": "customer_profile_memory"}])

        client.ensure_memory_store(_memory_agent_fixture(), _MODEL, "text-embedding-3-small")

        create_call = client._request.call_args
        self.assertEqual("POST", create_call.args[0])
        self.assertEqual("/memory_stores", create_call.args[1])
        self.assertEqual(MEMORY_API_VERSION, create_call.kwargs["api_version"])
        self.assertNotEqual(MEMORY_API_VERSION, "v1")

    def test_unexpected_error_is_not_swallowed(self):
        client = self._client()
        client._request = Mock(
            side_effect=HTTPError("https://example.test", 403, "Forbidden", {}, None)
        )

        with self.assertRaises(HTTPError):
            client.ensure_memory_store(_memory_agent_fixture(), _MODEL, "text-embedding-3-small")


class ToolboxDeploymentTests(unittest.TestCase):
    def _toolbox(self):
        return ToolboxDefinition(
            name="banking-toolbox",
            tools=[{"type": "code_interpreter"}, {"type": "toolbox_search"}],
        )

    def test_toolbox_is_optional(self):
        with patch.dict(os.environ, {}, clear=True):
            self.assertIsNone(_toolbox())

    def test_toolbox_requires_tools(self):
        with patch.dict(os.environ, {"TOOLBOX_NAME": "banking-toolbox"}, clear=True):
            with self.assertRaises(KeyError):
                _toolbox()

    def test_toolbox_rejects_untyped_tools(self):
        env = {"TOOLBOX_NAME": "banking-toolbox", "TOOLBOX_TOOLS": json.dumps([{"name": "x"}])}
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(ValueError):
                _toolbox()

    def test_toolbox_rejects_multiple_tools_without_identifiers(self):
        """Foundry returns 400 when more than one tool lacks an identifier.

        Catching it here names the offending tool types instead of surfacing a
        urllib traceback from POST /toolboxes/<name>/versions.
        """
        env = {
            "TOOLBOX_NAME": "banking-toolbox",
            "TOOLBOX_TOOLS": json.dumps(
                [{"type": "code_interpreter"}, {"type": "toolbox_search"}]
            ),
        }
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(ValueError) as caught:
                _toolbox()

        self.assertIn("code_interpreter", str(caught.exception))
        self.assertIn("toolbox_search", str(caught.exception))

    def test_toolbox_allows_a_single_tool_without_an_identifier(self):
        env = {
            "TOOLBOX_NAME": "banking-toolbox",
            "TOOLBOX_TOOLS": json.dumps(
                [{"type": "code_interpreter", "name": "calc"}, {"type": "toolbox_search"}]
            ),
        }
        with patch.dict(os.environ, env, clear=True):
            self.assertIsNotNone(_toolbox())

    def test_toolbox_rejects_duplicate_tool_identifiers(self):
        env = {
            "TOOLBOX_NAME": "banking-toolbox",
            "TOOLBOX_TOOLS": json.dumps(
                [
                    {"type": "code_interpreter", "name": "same"},
                    {"type": "toolbox_search", "name": "same"},
                ]
            ),
        }
        with patch.dict(os.environ, env, clear=True):
            with self.assertRaises(ValueError) as caught:
                _toolbox()

        self.assertIn("same", str(caught.exception))

    def test_consumer_endpoint_is_version_independent(self):
        """The consumer endpoint must follow the default version.

        A version-pinned URL would mean promoting a toolbox version silently
        does nothing until every agent is redeployed.
        """
        url = self._toolbox().mcp_url(_ENDPOINT)

        self.assertEqual(f"{_ENDPOINT}/toolboxes/banking-toolbox/mcp?api-version=v1", url)
        self.assertNotIn("/versions/", url)

    def test_mcp_tool_points_at_the_toolbox(self):
        tool = self._toolbox().mcp_tool(_ENDPOINT, "never")

        self.assertEqual("mcp", tool["type"])
        self.assertEqual("banking_toolbox", tool["server_label"])
        self.assertIn("/toolboxes/banking-toolbox/mcp", tool["server_url"])
        self.assertEqual("never", tool["require_approval"])

    def test_prompt_agent_keeps_memory_tool_when_toolbox_is_attached(self):
        """Attaching the toolbox must not displace the memory tool."""
        extra = [self._toolbox().mcp_tool(_ENDPOINT, "never")]
        definition = _memory_agent_fixture().definition(_MODEL, extra)

        types = [tool["type"] for tool in definition["tools"]]
        self.assertEqual(["memory_search_preview", "mcp"], types)

    def test_attaching_a_toolbox_forces_a_new_agent_version(self):
        agent = _memory_agent_fixture()
        without = agent.definition(_MODEL)
        with_toolbox = agent.definition(_MODEL, [self._toolbox().mcp_tool(_ENDPOINT, "never")])

        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = _ENDPOINT
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value={"value": [{"version": "3", "status": "active", "definition": without}]}
        )

        self.assertIsNone(client._find_matching_prompt_version(agent, with_toolbox))

    def test_unchanged_tool_set_does_not_create_a_new_version(self):
        toolbox = self._toolbox()
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = _ENDPOINT
        client._request = Mock(
            return_value={"value": [{"version": "1", "tools": toolbox.tools}]}
        )

        self.assertEqual("1", client.ensure_toolbox(toolbox))
        self.assertEqual(1, client._request.call_count)

    def test_changed_tool_set_creates_a_new_version(self):
        toolbox = self._toolbox()
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = _ENDPOINT
        client._request = Mock(
            side_effect=[
                {"value": [{"version": "1", "tools": [{"type": "code_interpreter"}]}]},
                {"version": "2"},
            ]
        )

        self.assertEqual("2", client.ensure_toolbox(toolbox))
        create_call = client._request.call_args
        self.assertEqual("POST", create_call.args[0])
        self.assertEqual("/toolboxes/banking-toolbox/versions", create_call.args[1])
        self.assertEqual(toolbox.tools, create_call.args[2]["tools"])

    def test_absent_toolbox_is_created(self):
        toolbox = self._toolbox()
        client = FoundryClient.__new__(FoundryClient)
        client._endpoint = _ENDPOINT
        client._request = Mock(
            side_effect=[HTTPError(_ENDPOINT, 404, "Not Found", {}, None), {"version": "1"}]
        )

        self.assertEqual("1", client.ensure_toolbox(toolbox))


if __name__ == "__main__":
    unittest.main()
