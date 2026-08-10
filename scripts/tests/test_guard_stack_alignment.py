"""Deterministic tests for scripts/guard-stack-alignment.sh.

The guard prevents a stale apps Terraform state from sending a Container Apps
Job into a previous environment.  That failure mode was observed in practice:
`app:migrate` started the migration job from a four-day-old apps state, so the
job ran in the old resource group and connected to the old PostgreSQL server,
surfacing only as an opaque Npgsql TCP timeout.

These tests put a fake `terraform` executable on PATH so no Terraform state,
Azure credentials, or network access are required.  They assert:

  - aligned stacks exit 0 so the calling script proceeds;
  - a stale apps state exits non-zero and names both resource groups;
  - a missing apps state exits non-zero and points at `task app:apply`;
  - a missing infrastructure state exits non-zero and points at `task cloud:up`.
"""
from __future__ import annotations

import os
import subprocess
import tempfile
import unittest
from pathlib import Path

_GUARD = Path(__file__).resolve().parents[1] / "guard-stack-alignment.sh"

# Emulates `terraform -chdir=<dir> output -raw <NAME>` for each scenario.
_FAKE_TERRAFORM = """#!/usr/bin/env bash
chdir=""; out=""
while [ $# -gt 0 ]; do
  case "$1" in
    -chdir=*) chdir="${1#-chdir=}";;
    -raw) out="$2"; shift;;
  esac
  shift
done
case "$SCENARIO" in
  aligned)
    [ "$chdir" = "./infrastructure" ] && [ "$out" = "APP_NAME" ] && echo "bluejay-15765" && exit 0
    [ "$chdir" = "./apps" ] && [ "$out" = "APPS_RESOURCE_GROUP_NAME" ] && echo "bluejay-15765-apps-rg" && exit 0
    ;;
  stale)
    [ "$chdir" = "./infrastructure" ] && [ "$out" = "APP_NAME" ] && echo "bluejay-15765" && exit 0
    [ "$chdir" = "./apps" ] && [ "$out" = "APPS_RESOURCE_GROUP_NAME" ] && echo "clam-54052-apps-rg" && exit 0
    ;;
  no_apps_state)
    [ "$chdir" = "./infrastructure" ] && [ "$out" = "APP_NAME" ] && echo "bluejay-15765" && exit 0
    exit 1
    ;;
  no_infrastructure_state)
    exit 1
    ;;
esac
exit 1
"""


class GuardStackAlignmentTests(unittest.TestCase):
    def _run(self, scenario: str) -> subprocess.CompletedProcess[str]:
        with tempfile.TemporaryDirectory() as tmp:
            fake_bin = Path(tmp) / "bin"
            fake_bin.mkdir()
            terraform = fake_bin / "terraform"
            terraform.write_text(_FAKE_TERRAFORM)
            terraform.chmod(0o755)

            env = dict(os.environ)
            env["PATH"] = f"{fake_bin}{os.pathsep}{env['PATH']}"
            env["SCENARIO"] = scenario

            return subprocess.run(
                ["bash", "-c", f"source {_GUARD}; assert_stacks_aligned"],
                capture_output=True,
                text=True,
                env=env,
                check=False,
            )

    def test_aligned_stacks_pass(self) -> None:
        result = self._run("aligned")
        self.assertEqual(result.returncode, 0, result.stderr)

    def test_stale_apps_state_is_rejected(self) -> None:
        result = self._run("stale")
        self.assertNotEqual(result.returncode, 0)
        # Both the expected and the actual group must appear so the operator can
        # see exactly which environment the state is stuck on.
        self.assertIn("bluejay-15765-apps-rg", result.stderr)
        self.assertIn("clam-54052-apps-rg", result.stderr)
        self.assertIn("task app:apply", result.stderr)

    def test_missing_apps_state_is_rejected(self) -> None:
        result = self._run("no_apps_state")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("task app:apply", result.stderr)

    def test_missing_infrastructure_state_is_rejected(self) -> None:
        result = self._run("no_infrastructure_state")
        self.assertNotEqual(result.returncode, 0)
        self.assertIn("task cloud:up", result.stderr)


if __name__ == "__main__":
    unittest.main()
