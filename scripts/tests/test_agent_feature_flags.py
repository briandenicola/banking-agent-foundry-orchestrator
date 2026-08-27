"""Guards that the agent memory and toolbox features have a real switch.

Both features were first shipped as "opt-in", but that was only true of the
Python deployer: `apps/` set `MEMORY_STORE_NAME` and `TOOLBOX_NAME`
unconditionally, so any `app:apply` turned them on. The claim in the docs and
the behaviour of the deployment had drifted apart.

These tests pin the switch end to end -- variable, gate, and task plumbing --
so the drift cannot come back silently. They read files only; no Terraform,
az CLI, or Azure access is required.
"""

from __future__ import annotations

import re
import unittest
from pathlib import Path


REPOSITORY_ROOT = Path(__file__).resolve().parents[2]
VARIABLES_TF = REPOSITORY_ROOT / "apps" / "variables.tf"
MAIN_TF = REPOSITORY_ROOT / "apps" / "main.tf"
DEPLOYER_TF = REPOSITORY_ROOT / "apps" / "agent-deployer.tf"
TASKFILE = REPOSITORY_ROOT / "tasks" / "Taskfile.app.yml"

FLAGS = ("enable_agent_memory", "enable_agent_toolbox")


def _variable_block(name: str) -> str:
    text = VARIABLES_TF.read_text(encoding="utf-8")
    match = re.search(rf'variable "{name}" \{{(.*?)\n\}}', text, re.DOTALL)
    if match is None:
        raise AssertionError(f"apps/variables.tf does not declare variable {name!r}")
    return match.group(1)


class FeatureFlagsAreDeclaredTests(unittest.TestCase):
    def test_both_flags_exist(self):
        for flag in FLAGS:
            with self.subTest(flag=flag):
                self.assertIn("type        = bool", _variable_block(flag))

    def test_both_flags_default_off(self):
        """Off by default.

        Memory retains model-extracted customer detail in a preview-tier store,
        so a deployment must not acquire it merely by running app:apply.
        """
        for flag in FLAGS:
            with self.subTest(flag=flag):
                self.assertIn("default     = false", _variable_block(flag))

    def test_flags_are_documented(self):
        for flag in FLAGS:
            with self.subTest(flag=flag):
                self.assertIn("description", _variable_block(flag))


class FeatureFlagsGateConfigurationTests(unittest.TestCase):
    def test_memory_store_name_is_gated(self):
        text = MAIN_TF.read_text(encoding="utf-8")
        self.assertRegex(
            text,
            r"memory_store_name\s*=\s*var\.enable_agent_memory\s*\?",
        )

    def test_toolbox_name_is_gated(self):
        text = MAIN_TF.read_text(encoding="utf-8")
        self.assertRegex(
            text,
            r"toolbox_name\s*=\s*var\.enable_agent_toolbox\s*\?",
        )

    def test_disabled_gates_resolve_to_empty_string(self):
        """The deployer treats an empty name as 'feature off'.

        Any other falsy placeholder would be read as a real store or toolbox
        name and would switch the feature back on.
        """
        text = MAIN_TF.read_text(encoding="utf-8")
        for local in ("memory_store_name", "toolbox_name"):
            with self.subTest(local=local):
                match = re.search(rf"{local}\s*=\s*var\.\w+\s*\?[^\n]*", text)
                self.assertIsNotNone(match)
                self.assertTrue(
                    match.group(0).rstrip().endswith(': ""'),
                    f"{local} must fall back to an empty string, got: {match.group(0)}",
                )

    def test_deployer_job_reads_the_gated_locals(self):
        """The job must consume the gated locals, not literals."""
        text = DEPLOYER_TF.read_text(encoding="utf-8")
        self.assertIn("value = local.memory_store_name", text)
        self.assertIn("value = local.toolbox_name", text)


class ApplyTaskPassesFlagsTests(unittest.TestCase):
    def test_apply_passes_both_flags(self):
        text = TASKFILE.read_text(encoding="utf-8")
        for flag in FLAGS:
            with self.subTest(flag=flag):
                self.assertIn(f'-var "{flag}={{{{.{flag.upper()}}}}}"', text)

    def test_apply_defaults_both_flags_off(self):
        text = TASKFILE.read_text(encoding="utf-8")
        for flag in FLAGS:
            with self.subTest(flag=flag):
                self.assertIn(
                    f'{flag.upper()}: \'{{{{default "false" .{flag.upper()}}}}}\'',
                    text,
                )


if __name__ == "__main__":
    unittest.main()
