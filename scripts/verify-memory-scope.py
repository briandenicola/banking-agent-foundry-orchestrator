#!/usr/bin/env python3
"""Prove — or disprove — that the profile agent's memory can be scoped per user.

Why this exists
---------------
The `customer-profile` agent is deployed with `scope = "{{$userId}}"`, a
template Foundry resolves from the *caller's* token. The orchestrator calls
Foundry with its own managed identity, and workflows execute in the background
long after the requesting user's token is gone, so every customer would land in
one shared memory scope. In a banking assistant that is a cross-customer data
leak, not a rough edge.

The fix under evaluation is for the orchestrator to assert the scope itself:
authenticate the user at the edge, then tell Foundry which scope to read and
write. This script establishes whether Foundry actually honours that.

What it checks, and why that specific check
-------------------------------------------
A request succeeding proves nothing. An earlier probe passed `user: "alice"`
alongside `agent_reference`; Foundry accepted the request, returned 200, and
silently ignored the field — the scope never changed. Any test that only
asserts "the call worked" would have called that a success.

So this asserts *isolation* instead, which is the property the design actually
depends on:

    1. write a distinctive fact under scope A
    2. read back under scope A  -> the fact must be recalled
    3. read back under scope B  -> the fact must NOT be recalled

Step 3 is the one that matters. Step 2 passing while step 3 fails is precisely
the silent-ignore failure, and it is reported as a failure here rather than a
pass, because shipping on it would leak one customer's memories to another.

Two strategies are tried, best first:

  agent_reference  Keeps Terraform as the single source of truth for the agent
                   definition. Only viable if Foundry accepts a scope override
                   next to an agent reference.

  inline           Reads the deployed agent's own definition back out of
                   Foundry and re-posts it with the memory tool's `scope`
                   replaced. More moving parts, but it still uses the deployed
                   definition verbatim rather than duplicating instructions.

Usage:
    python ./scripts/verify-memory-scope.py
    python ./scripts/verify-memory-scope.py --strategy inline

Exit code is 0 only if some strategy achieved real isolation.
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import time
import uuid
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
TOKEN_SCOPE = "https://ai.azure.com/.default"
AGENT_NAME = "customer-profile"
AGENTS_API_VERSION = "v1"

# Memory extraction is a background step after each turn. The deployment sets
# the update delay to 0, but the extraction itself still takes a moment, so the
# write is given time to land before anything reads it back.
SETTLE_SECONDS = 20

BOLD = "\033[1m"
DIM = "\033[2m"
GREEN = "\033[32m"
YELLOW = "\033[33m"
RED = "\033[31m"
RESET = "\033[0m"


class ProbeError(RuntimeError):
    pass


def terraform_output(directory: str, name: str) -> str:
    result = subprocess.run(
        ["terraform", f"-chdir={directory}", "output", "-raw", name],
        cwd=REPOSITORY_ROOT,
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def azure_token() -> str:
    result = subprocess.run(
        ["az", "account", "get-access-token", "--scope", TOKEN_SCOPE,
         "--query", "accessToken", "-o", "tsv"],
        check=True,
        capture_output=True,
        text=True,
    )
    token = result.stdout.strip()
    if not token:
        raise ProbeError("az returned an empty access token; run `az login` first")
    return token


def call(endpoint: str, path: str, body: dict[str, Any] | None = None,
         api_version: str | None = None) -> dict[str, Any]:
    url = f"{endpoint.rstrip('/')}{path}"
    if api_version:
        url = f"{url}?api-version={api_version}"

    payload = json.dumps(body).encode("utf-8") if body is not None else None
    http_request = Request(
        url,
        data=payload,
        method="POST" if payload is not None else "GET",
        headers={
            "Authorization": f"Bearer {azure_token()}",
            "Content-Type": "application/json",
            "Accept": "application/json",
        },
    )

    try:
        with urlopen(http_request, timeout=180) as response:
            return json.loads(response.read().decode("utf-8") or "{}")
    except HTTPError as error:
        detail = error.read().decode("utf-8", errors="replace") if error.fp else ""
        raise ProbeError(f"HTTP {error.code} from {path}: {detail[:600]}") from error
    except URLError as error:
        raise ProbeError(f"{path} failed: {error}") from error


def answer_text(response: dict[str, Any]) -> str:
    parts = [
        content.get("text", "")
        for item in response.get("output", [])
        if item.get("type") == "message"
        for content in item.get("content", [])
        if content.get("type") == "output_text"
    ]
    return "\n".join(parts).strip()


def recalled(response: dict[str, Any]) -> list[str]:
    """Memories the tool actually returned, read off the response envelope.

    Deliberately not read from the model's prose: the model will happily repeat
    a fact it saw earlier in the same request, so its wording cannot distinguish
    "recalled from the store" from "restated from the prompt".
    """
    found: list[str] = []
    for item in response.get("output", []):
        if item.get("type") != "memory_search_call":
            continue
        for memory in item.get("memories") or []:
            text = memory.get("content") or memory.get("text") or ""
            if text:
                found.append(text)
    return found


def latest_definition(endpoint: str) -> dict[str, Any]:
    """The deployed agent's own definition, so the inline probe does not invent one."""
    response = call(endpoint, f"/agents/{AGENT_NAME}/versions", api_version=AGENTS_API_VERSION)
    versions = response.get("data") or response.get("value") or []
    if not versions:
        raise ProbeError(f"agent '{AGENT_NAME}' has no versions; deploy it first")

    def version_key(item: dict[str, Any]) -> int:
        try:
            return int(item.get("version", 0))
        except (TypeError, ValueError):
            return 0

    newest = max(versions, key=version_key)
    definition = newest.get("definition")
    if not isinstance(definition, dict):
        raise ProbeError("agent version carried no definition object")
    return definition


def scoped_definition(definition: dict[str, Any], scope: str) -> dict[str, Any]:
    """The deployed definition with the memory tool bound to `scope`.

    Every other tool is carried across, so this probes the request the
    orchestrator actually sends (see CustomerProfileClient.BuildScopedRequest)
    rather than a stripped-down variant that might pass where the real one
    fails. `code_interpreter` needs one fixup: deployed it carries no container,
    but sent inline the API rejects it without one.
    """
    body = json.loads(json.dumps(definition))
    tools = body.get("tools") or []
    scoped = 0

    for tool in tools:
        if tool.get("type") == "memory_search_preview":
            tool["scope"] = scope
            scoped += 1
        elif tool.get("type") == "code_interpreter" and "container" not in tool:
            tool["container"] = {"type": "auto"}

    if not scoped:
        raise ProbeError("no memory_search_preview tool found on the deployed agent")

    body["tools"] = tools
    body.pop("kind", None)
    return body


def ask(endpoint: str, strategy: str, scope: str, message: str,
        definition: dict[str, Any]) -> dict[str, Any]:
    if strategy == "agent_reference":
        body = {
            "agent_reference": {"type": "agent_reference", "name": AGENT_NAME, "scope": scope},
            "input": message,
        }
    else:
        body = dict(scoped_definition(definition, scope))
        body["input"] = message
    return call(endpoint, "/openai/v1/responses", body)


def probe(endpoint: str, strategy: str, definition: dict[str, Any]) -> bool:
    """Write under one scope, then check both scopes. Returns True on isolation."""
    marker = uuid.uuid4().hex[:8]
    scope_a = f"probe-a-{marker}"
    scope_b = f"probe-b-{marker}"
    secret = f"my account nickname is Zarquon{marker}"

    print(f"\n{BOLD}strategy: {strategy}{RESET}")
    print(f"{DIM}  scopes {scope_a} / {scope_b}{RESET}")

    try:
        print("  1. writing a distinctive fact under scope A ...")
        ask(endpoint, strategy, scope_a, f"Please remember that {secret}.", definition)
    except ProbeError as error:
        print(f"{YELLOW}  rejected: {error}{RESET}")
        return False

    print(f"{DIM}     waiting {SETTLE_SECONDS}s for extraction{RESET}")
    time.sleep(SETTLE_SECONDS)

    question = "What is my account nickname? If you do not know, say you do not know."

    try:
        print("  2. reading back under scope A ...")
        response_a = ask(endpoint, strategy, scope_a, question, definition)
        print("  3. reading back under scope B ...")
        response_b = ask(endpoint, strategy, scope_b, question, definition)
    except ProbeError as error:
        print(f"{YELLOW}  rejected on read: {error}{RESET}")
        return False

    recalled_a = recalled(response_a)
    recalled_b = recalled(response_b)
    hit_a = any(marker in text for text in recalled_a)
    hit_b = any(marker in text for text in recalled_b)

    print(f"{DIM}     scope A recalled {len(recalled_a)} memories, marker present: {hit_a}{RESET}")
    print(f"{DIM}     scope B recalled {len(recalled_b)} memories, marker present: {hit_b}{RESET}")
    print(f"{DIM}     A said: {answer_text(response_a)[:140]}{RESET}")
    print(f"{DIM}     B said: {answer_text(response_b)[:140]}{RESET}")

    if hit_a and not hit_b:
        print(f"{GREEN}  PASS: the scope is honoured and the two scopes are isolated.{RESET}")
        return True
    if hit_a and hit_b:
        # The exact failure `user: "alice"` produced: accepted, 200, ignored.
        print(f"{RED}  FAIL: both scopes saw the fact. The scope was accepted but ignored.{RESET}")
        return False
    if not hit_a and not hit_b:
        print(f"{YELLOW}  INCONCLUSIVE: neither scope recalled it. The write may not have{RESET}")
        print(f"{YELLOW}  landed yet — re-run, or raise SETTLE_SECONDS.{RESET}")
        return False
    print(f"{RED}  FAIL: only scope B saw the fact, which makes no sense. Investigate.{RESET}")
    return False


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--strategy", choices=["agent_reference", "inline"],
                        help="probe one strategy instead of both")
    parser.add_argument("--endpoint", help="Foundry project endpoint (default: terraform output)")
    args = parser.parse_args()

    try:
        endpoint = args.endpoint or terraform_output("infrastructure", "FOUNDRY_PROJECT_ENDPOINT")
    except subprocess.CalledProcessError:
        print(f"{RED}Could not read the project endpoint from terraform. "
              f"Pass --endpoint explicitly.{RESET}", file=sys.stderr)
        return 2

    print(f"{BOLD}Verifying per-user memory scoping against{RESET} {endpoint}")

    try:
        definition = latest_definition(endpoint)
    except ProbeError as error:
        print(f"{RED}{error}{RESET}", file=sys.stderr)
        return 2

    strategies = [args.strategy] if args.strategy else ["agent_reference", "inline"]
    for strategy in strategies:
        if probe(endpoint, strategy, definition):
            print(f"\n{GREEN}{BOLD}Use the '{strategy}' strategy for per-user memory.{RESET}")
            return 0

    print(f"\n{YELLOW}{BOLD}No strategy achieved isolation.{RESET}")
    print(f"{YELLOW}Per-user memory is not available; the profile step must fall back to a{RESET}")
    print(f"{YELLOW}single shared scope, and the demo has to be narrated as one customer.{RESET}")
    return 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except KeyboardInterrupt:
        sys.exit(130)
