import json
import unittest
from unittest.mock import Mock

from deploy import AgentDefinition, FoundryClient, _definitions, _items


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

    def test_matching_version_reuses_creating_deployment(self):
        client = FoundryClient.__new__(FoundryClient)
        client._agent_exists = Mock(return_value=True)
        client._request = Mock(
            return_value={
                "value": [
                    {
                        "version": "2",
                        "status": "creating",
                        "definition": {
                            "container_configuration": {
                                "image": "example.azurecr.io/hosted-agents:abc123"
                            },
                            "environment_variables": {
                                "BANKING_AGENT_KIND": "workflow-planning",
                                "AZURE_AI_MODEL_DEPLOYMENT_NAME": "gpt-5.4-mini",
                            },
                        },
                    }
                ]
            }
        )

        result = client._find_matching_version(
            AgentDefinition("workflow-planning", "workflow-planning"),
            "example.azurecr.io/hosted-agents:abc123",
            "gpt-5.4-mini",
        )

        self.assertEqual(("2", "creating"), result)


if __name__ == "__main__":
    unittest.main()
