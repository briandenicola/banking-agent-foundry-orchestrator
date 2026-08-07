import json
import os
import unittest
from unittest.mock import Mock, patch

from deploy import (
    INVOKE_TIMEOUT_ENV_VAR,
    INVOKE_TIMEOUT_SECONDS,
    AgentDefinition,
    FoundryClient,
    _definitions,
    _items,
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


if __name__ == "__main__":
    unittest.main()
