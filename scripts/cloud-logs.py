#!/usr/bin/env python3
"""Read the deployed stack's logs and model-call health as JSON.

Output is a single JSON document on stdout and nothing else, so it can be
piped straight into jq. Progress notes and errors go to stderr.

Two things this deliberately does not do.

It does not omit a section that found nothing. A report that drops the
rate-limit key when there are no 429s is indistinguishable from one whose
query broke, and `jq '.rate_limited.count'` should answer the question rather
than return null. Every section carries its own count, and the window it was
measured over.

It does not merge numbers from different layers. Foundry's own HTTP edge and
the orchestrator's outbound dependency calls disagree about failure counts,
and both are right -- they measure different things. They stay under separate
keys, labelled with their source, rather than being added into one figure that
is true of nothing.
"""
from __future__ import annotations

import argparse
import json
import os
import re
import subprocess
import sys
import time
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parent))

from azure_logs import (  # noqa: E402
    AzureQueryError,
    resolve_container_app,
    resolve_workspace_id,
    run_kql,
)

REPOSITORY_ROOT = Path(__file__).resolve().parents[1]

DEFAULT_APP = "orchestrator"
DEFAULT_SINCE = "30m"
DEFAULT_LIMIT = 100

#: Agent invocations are HTTP POSTs whose path carries the agent name. The
#: agent is the closest thing this system has to a "phase": workflow-planning
#: is the planner, customer-profile is the memory recall, and the other three
#: are specialists.
AGENT_PATH = re.compile(r"/agents/(?P<agent>[a-z0-9-]+)/")

PHASES = {
    "workflow-planning": "planner",
    "customer-profile": "profile_recall",
    "transaction-explanation": "specialist",
    "suspicious-activity": "specialist",
    "dispute-planning": "specialist",
}

SUCCESS_STATUSES = {"200", "201", "202", "204"}


def parse_since(value: str) -> str:
    """Validate a KQL-safe lookback such as ``30m``, ``2h``, or ``7d``."""
    if not re.fullmatch(r"\d+[mhd]", value):
        raise argparse.ArgumentTypeError(
            f"Invalid lookback '{value}'. Use a number followed by m, h, or d, "
            "for example 30m, 2h, or 7d."
        )
    return value


def kql_string(value: str) -> str:
    """Escape a value for embedding in a KQL string literal."""
    return value.replace("\\", "\\\\").replace("'", "\\'")


def emit(document: dict) -> None:
    json.dump(document, sys.stdout, indent=2, sort_keys=False)
    sys.stdout.write("\n")


def as_int(value: object) -> int:
    try:
        return int(float(value))  # type: ignore[arg-type]
    except (TypeError, ValueError):
        return 0


def failure_rate(failures: int, calls: int) -> float | None:
    """Rate as a percentage, or None when there is nothing to divide by.

    Rounded to one decimal rather than zero, because three failures in six
    hundred calls is 0.5% and a whole-number rounding prints 0 next to a
    non-zero count.
    """
    if not calls:
        return None
    return round(failures / calls * 100, 1)


# ---------------------------------------------------------------------------
# Commands
# ---------------------------------------------------------------------------

def command_recent(args: argparse.Namespace, resource_group: str) -> int:
    app = resolve_container_app(resource_group, args.app)
    workspace_id = resolve_workspace_id(resource_group, app)

    grep_filter = ""
    if args.grep:
        grep_filter = f'| where Log_s matches regex @"(?i){kql_string(args.grep)}"'

    query = (
        "ContainerAppConsoleLogs_CL "
        f"| where TimeGenerated > ago({args.since}) "
        f"| where ContainerAppName_s == '{kql_string(app)}' "
        f"{grep_filter} "
        "| project TimeGenerated, RevisionName_s, Log_s "
        "| order by TimeGenerated desc "
        f"| take {args.limit}"
    )
    entries = run_kql(workspace_id, query)
    entries.reverse()

    emit(
        {
            "app": app,
            "resource_group": resource_group,
            "window": args.since,
            "grep": args.grep,
            "limit": args.limit,
            "count": len(entries),
            "truncated": len(entries) >= args.limit,
            "entries": [
                {
                    "timestamp": entry.get("TimeGenerated"),
                    "revision": str(entry.get("RevisionName_s", "")).rsplit("--", 1)[-1],
                    "revision_full": entry.get("RevisionName_s"),
                    "log": " ".join(str(entry.get("Log_s", "")).split()),
                }
                for entry in entries
            ],
        }
    )
    return 0


def command_model(args: argparse.Namespace, resource_group: str) -> int:
    app = resolve_container_app(resource_group, DEFAULT_APP)
    workspace_id = resolve_workspace_id(resource_group, app)
    window = args.since

    # ---- Agent invocations, by phase -------------------------------------
    dependencies = run_kql(
        workspace_id,
        "AppDependencies "
        f"| where TimeGenerated > ago({window}) "
        '| where Name has "/agents/" and Name has "invocations" '
        "| summarize calls=count(), failures=countif(Success == false), "
        "p95_ms=percentile(DurationMs, 95) by Name "
        "| order by calls desc",
    )
    agents = []
    for entry in dependencies:
        match = AGENT_PATH.search(str(entry.get("Name", "")))
        agent = match.group("agent") if match else str(entry.get("Name", ""))
        calls = as_int(entry.get("calls"))
        failures = as_int(entry.get("failures"))
        agents.append(
            {
                "agent": agent,
                "phase": PHASES.get(agent, "unknown"),
                "calls": calls,
                "failures": failures,
                "failure_rate_percent": failure_rate(failures, calls),
                "p95_ms": round(float(entry.get("p95_ms") or 0)),
            }
        )

    # ---- The Foundry HTTP edge -------------------------------------------
    # Restricted to RequestResponse on purpose. The Audit and
    # AzureOpenAIRequestUsage categories carry no ResultSignature, so including
    # them reports successful calls under a blank status and they read as
    # failures.
    edge = run_kql(
        workspace_id,
        "AzureDiagnostics "
        f"| where TimeGenerated > ago({window}) "
        '| where ResourceProvider == "MICROSOFT.COGNITIVESERVICES" '
        '| where Category == "RequestResponse" '
        "| where isnotempty(ResultSignature) "
        "| summarize requests=count() by ResultSignature, OperationName "
        "| order by requests desc",
    )
    edge_rows = [
        {
            "status": str(entry.get("ResultSignature", "")),
            "operation": str(entry.get("OperationName", "")),
            "requests": as_int(entry.get("requests")),
        }
        for entry in edge
    ]
    edge_total = sum(row["requests"] for row in edge_rows)
    throttled = [row for row in edge_rows if row["status"] == "429"]
    other_failures = [
        row
        for row in edge_rows
        if row["status"] not in SUCCESS_STATUSES and row["status"] != "429"
    ]

    # ---- Model usage ------------------------------------------------------
    usage = run_kql(
        workspace_id,
        "AzureDiagnostics "
        f"| where TimeGenerated > ago({window}) "
        '| where Category == "AzureOpenAIRequestUsage" '
        "| summarize calls=count() by OperationName "
        "| order by calls desc",
    )

    # ---- Recent exceptions ------------------------------------------------
    errors = run_kql(
        workspace_id,
        "AppExceptions "
        f"| where TimeGenerated > ago({window}) "
        "| project TimeGenerated, ProblemId, OuterMessage, AppRoleName "
        "| order by TimeGenerated desc "
        f"| take {args.limit}",
    )

    emit(
        {
            "window": window,
            "resource_group": resource_group,
            "workspace_behind": app,
            "agent_invocations": {
                "source": "AppDependencies: the orchestrator's own view, so a failure "
                "here is one a workflow actually saw",
                "count": len(agents),
                "total_calls": sum(a["calls"] for a in agents),
                "total_failures": sum(a["failures"] for a in agents),
                "by_agent": agents,
            },
            "rate_limited": {
                "source": "AzureDiagnostics RequestResponse: Foundry's own HTTP surface, "
                "a different layer from agent_invocations. Do not add the two together",
                "count": sum(row["requests"] for row in throttled),
                "requests_seen": edge_total,
                "diagnostics_delivering": edge_total > 0,
                "by_operation": throttled,
            },
            "edge_failures": {
                "source": "AzureDiagnostics RequestResponse, excluding 429",
                "count": sum(row["requests"] for row in other_failures),
                "by_operation": other_failures,
            },
            "model_usage": {
                "source": "AzureDiagnostics AzureOpenAIRequestUsage. Memory extraction is "
                "a second model pass, so a memory-enabled stack shows more completions "
                "than workflows",
                "count": len(usage),
                "by_operation": [
                    {
                        "operation": str(entry.get("OperationName", "")),
                        "calls": as_int(entry.get("calls")),
                    }
                    for entry in usage
                ],
            },
            "exceptions": {
                "source": "AppExceptions, newest first",
                "count": len(errors),
                "truncated": len(errors) >= args.limit,
                "recent": [
                    {
                        "timestamp": entry.get("TimeGenerated"),
                        "role": entry.get("AppRoleName"),
                        "problem": entry.get("ProblemId"),
                        "message": " ".join(str(entry.get("OuterMessage", "")).split()),
                    }
                    for entry in errors
                ],
            },
        }
    )
    return 0


#: ``az containerapp logs show`` prefixes every connection with two status
#: lines that are not container output. They would otherwise be re-emitted on
#: each poll.
PREAMBLE = ("Connecting to the container", "Successfully Connected to container")

#: Bound on the set of already-emitted lines. Large enough that a burst cannot
#: cycle a line out and back in within one interval, small enough to stay flat
#: over a long session.
SEEN_LIMIT = 2000


def read_tail(app: str, resource_group: str, tail: int) -> list[dict]:
    """One non-streaming read of the container's recent output.

    ``--follow`` is deliberately not used. It opens a websocket to the log
    stream endpoint, which is not reachable from every network; when it is
    blocked the command connects, emits nothing, and never exits, which looks
    exactly like an idle application. Polling this way costs a little latency
    and works over ordinary HTTPS.
    """
    result = subprocess.run(
        [
            "az", "containerapp", "logs", "show",
            "--name", app,
            "--resource-group", resource_group,
            "--tail", str(tail),
        ],
        capture_output=True,
        text=True,
    )
    if result.returncode != 0:
        raise AzureQueryError(result.stderr.strip() or "Unable to read container logs.")

    entries = []
    for line in result.stdout.splitlines():
        line = line.strip()
        if not line:
            continue
        try:
            entry = json.loads(line)
        except json.JSONDecodeError:
            continue
        if any(marker in str(entry.get("Log", "")) for marker in PREAMBLE):
            continue
        entries.append(entry)
    return entries


def command_follow(args: argparse.Namespace, resource_group: str) -> int:
    """Stream new log lines as one JSON object per line.

    A single JSON document could not be closed until the stream ended, so a
    follower emits JSON Lines instead; `jq` consumes that incrementally.
    """
    app = resolve_container_app(resource_group, args.app)
    print(
        f"Following {app} every {args.interval}s. Ctrl-C to stop.",
        file=sys.stderr,
        flush=True,
    )

    seen: dict[str, None] = {}
    first_pass = True
    try:
        while True:
            for entry in read_tail(app, resource_group, args.limit):
                key = f"{entry.get('TimeStamp')}|{entry.get('Log')}"
                if key in seen:
                    continue
                seen[key] = None
                text = str(entry.get("Log", ""))
                # The stream marks each line with a stdout/stderr letter.
                if len(text) > 2 and text[0] in "FE" and text[1] == " ":
                    text = text[2:]
                json.dump(
                    {
                        "timestamp": entry.get("TimeStamp"),
                        "app": app,
                        "log": " ".join(text.split()),
                        "backfill": first_pass,
                    },
                    sys.stdout,
                )
                sys.stdout.write("\n")
                sys.stdout.flush()

            while len(seen) > SEEN_LIMIT:
                seen.pop(next(iter(seen)))
            first_pass = False
            time.sleep(args.interval)
    except KeyboardInterrupt:
        print("Stopped.", file=sys.stderr)
        return 0
    except BrokenPipeError:
        return 0


def resource_group_name() -> str:
    configured = os.environ.get("APPS_RESOURCE_GROUP_NAME", "").strip()
    if configured:
        return configured
    result = subprocess.run(
        ["terraform", "-chdir=apps", "output", "-raw", "APPS_RESOURCE_GROUP_NAME"],
        cwd=REPOSITORY_ROOT,
        capture_output=True,
        text=True,
    )
    name = result.stdout.strip()
    if result.returncode != 0 or not name:
        raise AzureQueryError(
            "Could not determine the apps resource group. Set APPS_RESOURCE_GROUP_NAME "
            "or run this from a checkout whose Terraform state has been applied."
        )
    return name


def build_parser() -> argparse.ArgumentParser:
    parser = argparse.ArgumentParser(
        prog="cloud-logs",
        description="Read logs and model-call health from the deployed stack, as JSON.",
    )
    parser.add_argument(
        "app",
        nargs="?",
        default=DEFAULT_APP,
        help=f"Container App short name, for example orchestrator or webui (default: {DEFAULT_APP}).",
    )
    parser.add_argument("--since", type=parse_since, default=DEFAULT_SINCE, help="Lookback, e.g. 30m, 2h, 7d.")
    parser.add_argument("--limit", type=int, default=DEFAULT_LIMIT, help="Maximum rows per section.")
    parser.add_argument("--grep", help="Case-insensitive regular expression applied to the log text.")
    parser.add_argument("--follow", action="store_true", help="Stream instead of querying history.")
    parser.add_argument("--interval", type=float, default=5.0, help="Seconds between polls when following.")
    parser.add_argument("--model", action="store_true", help="Report model-call health instead of logs.")
    return parser


def main(argv: list[str] | None = None) -> int:
    args = build_parser().parse_args(argv)
    try:
        resource_group = resource_group_name()
        if args.model:
            return command_model(args, resource_group)
        if args.follow:
            return command_follow(args, resource_group)
        return command_recent(args, resource_group)
    except AzureQueryError as error:
        print(f"error: {error}", file=sys.stderr)
        return 1


if __name__ == "__main__":
    raise SystemExit(main())
