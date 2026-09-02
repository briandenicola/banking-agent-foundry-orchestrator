#!/usr/bin/env python3
"""Drive the `customer-profile` Foundry prompt agent for a live demonstration.

Why this exists
---------------
The four hosted agents are LangGraph containers the C# orchestrator calls over
MCP, so the smoke test exercises them and they show up in traces. The
`customer-profile` agent is a different thing: a Foundry *prompt* agent, where
Foundry runs the model loop, the managed memory tool, and the code interpreter
for us. Nothing in the application calls it yet -- see the backlog item for
wiring it into the workflow -- so without this script it is deployed but never
invoked, and produces no traces at all.

Each act below is a separate `POST /openai/v1/responses` call with no
`previous_response_id`, so the model has no conversational context to fall back
on. Anything it recalls in act 2 came out of the memory store, not the prompt.
That is the point of the demonstration, and it is why the acts are deliberately
separate requests rather than one conversation.

Usage:
    python ./scripts/demo-customer-profile.py            # run the full script
    python ./scripts/demo-customer-profile.py --act 2    # run one act
    python ./scripts/demo-customer-profile.py --show-memories
    python ./scripts/demo-customer-profile.py --reset    # forget this user
"""

from __future__ import annotations

import argparse
import json
import subprocess
import sys
import textwrap
import time
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.request import Request, urlopen


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
TOKEN_SCOPE = "https://ai.azure.com/.default"
AGENT_NAME = "customer-profile"
MEMORY_STORE_NAME = "customer_profile_memory"
MEMORY_API_VERSION = "2025-11-15-preview"

# Foundry extracts memories asynchronously after a turn. The deployment sets
# `memory_update_delay_seconds = 0`, but extraction is still a background step,
# so a short settle keeps the live demonstration deterministic.
SETTLE_SECONDS = 12

# A rate limit is the one failure most likely to interrupt a live run, so it is
# retried rather than reported.
RETRY_ATTEMPTS = 6
RETRY_BASE_SECONDS = 20

BOLD = "\033[1m"
DIM = "\033[2m"
GREEN = "\033[32m"
YELLOW = "\033[33m"
RED = "\033[31m"
CYAN = "\033[36m"
RESET = "\033[0m"


class DemoFailure(RuntimeError):
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


def azure_token(scope: str) -> str:
    result = subprocess.run(
        ["az", "account", "get-access-token", "--scope", scope, "--query", "accessToken", "-o", "tsv"],
        check=True,
        capture_output=True,
        text=True,
    )
    return result.stdout.strip()


def request(endpoint: str, path: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
    """Call the project API, retrying the failures a live audience would see.

    Memory extraction runs the chat model again after every turn, so a demo
    burns roughly twice the tokens the visible conversation suggests and can
    trip the deployment's per-minute limit. A 429 on stage looks exactly like a
    broken agent, so it is waited out rather than surfaced.
    """

    payload = json.dumps(body).encode("utf-8") if body is not None else None

    for attempt in range(1, RETRY_ATTEMPTS + 1):
        token = azure_token(TOKEN_SCOPE)
        http_request = Request(
            f"{endpoint.rstrip('/')}{path}",
            data=payload,
            method="POST" if payload is not None else "GET",
            headers={
                "Authorization": f"Bearer {token}",
                "Content-Type": "application/json",
                "Accept": "application/json",
            },
        )

        try:
            with urlopen(http_request, timeout=180) as response:
                return json.loads(response.read().decode("utf-8") or "{}")
        except HTTPError as error:
            detail = ""
            stream = getattr(error, "fp", None)
            if stream is not None:
                detail = error.read().decode("utf-8", errors="replace")

            retryable = error.code == 429 or error.code >= 500
            if not retryable or attempt == RETRY_ATTEMPTS:
                raise DemoFailure(f"{path} returned {error.code}: {detail}") from error

            delay = _retry_after(error) or RETRY_BASE_SECONDS * attempt
            label = "rate limited" if error.code == 429 else f"HTTP {error.code}"
            print(f"{DIM}    ({label}; retrying in {delay}s){RESET}", file=sys.stderr)
            time.sleep(delay)
        except URLError as error:
            if attempt == RETRY_ATTEMPTS:
                raise DemoFailure(f"{path} failed: {error}") from error
            time.sleep(RETRY_BASE_SECONDS * attempt)

    raise DemoFailure(f"{path} did not succeed after {RETRY_ATTEMPTS} attempts")


def _retry_after(error: HTTPError) -> int:
    """The server's own backoff hint, when it sends one."""
    raw = ""
    headers = getattr(error, "headers", None)
    if headers is not None:
        raw = str(headers.get("Retry-After", "") or "")
    try:
        return max(1, min(int(float(raw)), 120))
    except (TypeError, ValueError):
        return 0


def ask(endpoint: str, message: str) -> dict[str, Any]:
    """One turn, in its own conversation, against the prompt agent."""
    return request(
        endpoint,
        "/openai/v1/responses",
        {
            "agent_reference": {"type": "agent_reference", "name": AGENT_NAME},
            "input": message,
        },
    )


def wrap(text: str, indent: str = "    ") -> str:
    lines: list[str] = []
    for paragraph in text.splitlines():
        lines.extend(
            textwrap.wrap(paragraph, width=92, initial_indent=indent, subsequent_indent=indent)
            or [indent]
        )
    return "\n".join(lines)


def answer_text(response: dict[str, Any]) -> str:
    parts: list[str] = []
    for item in response.get("output", []):
        if item.get("type") != "message":
            continue
        for content in item.get("content", []):
            if content.get("type") == "output_text":
                parts.append(content.get("text", ""))
    return "\n".join(parts).strip()


def tool_calls(response: dict[str, Any]) -> list[dict[str, Any]]:
    return [item for item in response.get("output", []) if item.get("type") != "message"]


def recalled_memories(response: dict[str, Any]) -> list[dict[str, str]]:
    """Memories the memory tool actually retrieved for this turn.

    Read off the response rather than the model's prose, because the whole
    claim being demonstrated is about what is in the store.
    """
    found: list[dict[str, str]] = []
    seen: set[str] = set()
    for item in response.get("output", []):
        if item.get("type") != "memory_search_call":
            continue
        for memory in item.get("memories", []) or []:
            identifier = memory.get("memory_id", "")
            if identifier in seen:
                continue
            seen.add(identifier)
            found.append(
                {
                    "memory_id": identifier,
                    "kind": memory.get("kind", ""),
                    "content": memory.get("content", ""),
                    "scope": memory.get("scope", ""),
                }
            )
    return found


def memory_scope(response: dict[str, Any]) -> str:
    for tool in response.get("tools", []):
        if str(tool.get("type", "")).startswith("memory_search"):
            return str(tool.get("scope", ""))
    return ""


def show(title: str, prompt: str, response: dict[str, Any], *, show_memories: bool = False) -> None:
    print(f"\n{BOLD}{CYAN}{title}{RESET}")
    print(f"{DIM}{wrap('customer: ' + prompt)}{RESET}")

    calls = tool_calls(response)
    if calls:
        summary = ", ".join(sorted({str(call.get("type")) for call in calls}))
        print(f"{YELLOW}    [Foundry ran: {summary}]{RESET}")

    for call in calls:
        if call.get("type") == "code_interpreter_call" and call.get("code"):
            code = str(call["code"]).strip()
            print(f"{YELLOW}    [code interpreter] {code}{RESET}")

    print(f"{GREEN}{wrap(answer_text(response) or '(no text)')}{RESET}")

    if show_memories:
        memories = recalled_memories(response)
        print(f"\n{BOLD}    Memories the tool retrieved ({len(memories)}):{RESET}")
        for memory in memories:
            print(wrap(f"- [{memory['kind']}] {memory['content']}", indent="      "))


def settle(seconds: int = SETTLE_SECONDS) -> None:
    print(f"{DIM}    ...waiting {seconds}s for Foundry to extract memories...{RESET}")
    time.sleep(seconds)


ACTS: dict[int, dict[str, str]] = {
    1: {
        "title": "Act 1 - the customer states a servicing preference",
        "prompt": (
            "Please contact me by SMS only, never phone. I also need large-print "
            "statements because I have low vision."
        ),
        "narrative": (
            "Nothing is remembered yet. Watch the tool line: Foundry issues a "
            "memory command to write the preference, not just a chat completion."
        ),
    },
    2: {
        "title": "Act 2 - a NEW conversation. Does it remember?",
        "prompt": "How should you contact me, and is there anything I need for readability?",
        "narrative": (
            "This is a separate request with no previous_response_id, so there is "
            "no conversation history. Everything it says came from the memory store."
        ),
    },
    3: {
        "title": "Act 3 - the customer volunteers PII",
        "prompt": (
            "My card number is 4111 1111 1111 1111, my balance is 8,412.66 dollars, "
            "and my date of birth is 3 March 1979. Please prefer email for marketing."
        ),
        "narrative": (
            "The memory store is configured to retain servicing preferences only. "
            "The marketing preference should be kept; the card, balance and date of "
            "birth should not."
        ),
    },
    4: {
        "title": "Act 4 - prove what is actually in the store",
        "prompt": (
            "What do you remember about me? Also, here are my card spends this month: "
            "48.20, 12.99, 245.00, 7.45, 1120.30, 63.10, 18.75, 402.66, 9.99, 87.40, "
            "1560.05, 33.20, 74.90, 210.15, 5.60. Work out the total, the mean and the "
            "sample standard deviation, and list every spend more than two standard "
            "deviations above the mean. Show the Python you used."
        ),
        "narrative": (
            "The listed memories below are read from the memory_search_call in the "
            "API response, not from the model's prose, so this is the store's own "
            "account of itself. The statistics are deliberately beyond reliable "
            "mental arithmetic, which is what forces a real code_interpreter call "
            "rather than the model guessing: one response then shows memory and "
            "tools together."
        ),
    },
}


def run_act(endpoint: str, number: int) -> dict[str, Any]:
    act = ACTS[number]
    print(f"\n{DIM}{'=' * 96}{RESET}")
    print(wrap(act["narrative"], indent="  "))
    response = ask(endpoint, act["prompt"])
    show(act["title"], act["prompt"], response, show_memories=number == 4)
    return response


def show_memories(endpoint: str) -> int:
    response = ask(endpoint, "What do you remember about me?")
    memories = recalled_memories(response)
    scope = memory_scope(response)
    print(f"{BOLD}Memory scope:{RESET} {scope or '(not reported)'}")
    print(f"{BOLD}Stored memories ({len(memories)}):{RESET}")
    for memory in memories:
        print(wrap(f"- [{memory['kind']}] {memory['content']}", indent="  "))
    if not memories:
        print("  (none)")
    return 0


def reset(endpoint: str) -> int:
    """Clear the memory store so a rehearsal starts from nothing.

    The whole store is deleted and recreated from its own definition, rather
    than deleting memories one by one: the preview API rejects the `memory_id`
    values that memory search returns, so per-item deletion is not available.
    That makes this a blunt instrument -- it clears every scope, not just the
    caller's -- which is fine for a demonstration environment and wrong for a
    shared one. `task app:deploy-hosted-agents` recreates the store too, so
    nothing here is unrecoverable.
    """

    token = azure_token(TOKEN_SCOPE)
    base = f"{endpoint.rstrip('/')}/memory_stores"

    def call(method: str, path: str, body: dict[str, Any] | None = None) -> dict[str, Any]:
        payload = json.dumps(body).encode("utf-8") if body is not None else None
        headers = {"Authorization": f"Bearer {token}", "Accept": "application/json"}
        if payload is not None:
            headers["Content-Type"] = "application/json"
        http_request = Request(f"{path}", data=payload, method=method, headers=headers)
        try:
            with urlopen(http_request, timeout=60) as response:
                return json.loads(response.read().decode("utf-8") or "{}")
        except HTTPError as error:
            detail = ""
            stream = getattr(error, "fp", None)
            if stream is not None:
                detail = error.read().decode("utf-8", errors="replace")
            raise DemoFailure(f"{method} {path} returned {error.code}: {detail}") from error

    store_url = f"{base}/{MEMORY_STORE_NAME}?api-version={MEMORY_API_VERSION}"
    store = call("GET", store_url)
    definition = store.get("definition")
    if not definition:
        raise DemoFailure(f"Memory store {MEMORY_STORE_NAME} has no definition to restore.")

    call("DELETE", store_url)
    call(
        "POST",
        f"{base}?api-version={MEMORY_API_VERSION}",
        {
            "name": store.get("name", MEMORY_STORE_NAME),
            "description": store.get("description", ""),
            "definition": definition,
        },
    )
    print(f"Memory store {MEMORY_STORE_NAME} recreated. All memories cleared.")
    return 0


def main() -> int:
    parser = argparse.ArgumentParser(description=__doc__)
    parser.add_argument("--endpoint", default="", help="Foundry project endpoint")
    parser.add_argument("--act", type=int, choices=sorted(ACTS), help="Run a single act")
    parser.add_argument("--show-memories", action="store_true", help="List stored memories and exit")
    parser.add_argument("--reset", action="store_true", help="Delete this identity's memories")
    parser.add_argument(
        "--settle",
        type=int,
        default=SETTLE_SECONDS,
        help="Seconds to wait for memory extraction between acts",
    )
    arguments = parser.parse_args()

    endpoint = arguments.endpoint or terraform_output("infrastructure", "FOUNDRY_PROJECT_ENDPOINT")

    try:
        if arguments.show_memories:
            return show_memories(endpoint)
        if arguments.reset:
            return reset(endpoint)

        if arguments.act:
            run_act(endpoint, arguments.act)
            return 0

        print(f"{BOLD}Foundry memory and tools: the customer-profile agent{RESET}")
        print(f"{DIM}Project: {endpoint}{RESET}")

        for number in sorted(ACTS):
            run_act(endpoint, number)
            if number < max(ACTS):
                settle(arguments.settle)

        print(f"\n{DIM}{'=' * 96}{RESET}")
        print(f"{BOLD}Everything above ran on Entra ID. No API keys were used.{RESET}")
        return 0
    except DemoFailure as error:
        print(f"{RED}{error}{RESET}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    sys.exit(main())
