#!/usr/bin/env python3
from __future__ import annotations

import argparse
import http.cookiejar
import json
import os
import re
import subprocess
import sys
import time
from dataclasses import asdict, dataclass
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode
from urllib.request import HTTPCookieProcessor, Request, build_opener, urlopen


REPOSITORY_ROOT = Path(__file__).resolve().parents[1]
EXPECTED_AGENTS = (
    "workflow-planning",
    "transaction-explanation",
    "suspicious-activity",
    "dispute-planning",
)
READY_AGENT_STATUSES = {"active", "running"}
ANTIFORGERY_PATTERN = re.compile(
    r'name="__RequestVerificationToken" type="hidden" value="([^"]+)"'
)


@dataclass(frozen=True)
class CheckResult:
    name: str
    passed: bool
    duration_ms: int
    details: dict[str, Any]


class SmokeFailure(RuntimeError):
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


def setting(environment_name: str, terraform_directory: str, output_name: str) -> str:
    configured = os.environ.get(environment_name, "").strip()
    return configured or terraform_output(terraform_directory, output_name)


def azure_token(scope: str) -> str:
    result = subprocess.run(
        [
            "az",
            "account",
            "get-access-token",
            "--scope",
            scope,
            "--query",
            "accessToken",
            "--output",
            "tsv",
        ],
        check=True,
        capture_output=True,
        text=True,
    )
    token = result.stdout.strip()
    if not token:
        raise SmokeFailure(f"Azure CLI did not return a token for {scope}.")
    return token


def request_json(
    method: str,
    url: str,
    *,
    body: dict[str, Any] | None = None,
    token: str | None = None,
    timeout: int,
) -> tuple[int, dict[str, Any]]:
    payload = json.dumps(body).encode("utf-8") if body is not None else None
    headers = {
        "Accept": "application/json",
        "User-Agent": "banking-agent-mvp-smoke/1.0",
    }
    if payload is not None:
        headers["Content-Type"] = "application/json"
    if token:
        headers["Authorization"] = f"Bearer {token}"

    request = Request(url, data=payload, headers=headers, method=method)
    try:
        with urlopen(request, timeout=timeout) as response:
            response_body = response.read().decode("utf-8")
            return response.status, json.loads(response_body) if response_body else {}
    except HTTPError as error:
        response_body = error.read().decode("utf-8", errors="replace")
        try:
            parsed = json.loads(response_body) if response_body else {}
        except json.JSONDecodeError:
            parsed = {"body": response_body}
        return error.code, parsed


def run_check(name: str, operation) -> CheckResult:
    started = time.monotonic()
    try:
        details = operation()
        return CheckResult(
            name=name,
            passed=True,
            duration_ms=round((time.monotonic() - started) * 1000),
            details=details,
        )
    except Exception as error:
        return CheckResult(
            name=name,
            passed=False,
            duration_ms=round((time.monotonic() - started) * 1000),
            details={"error": str(error)},
        )


def expect_status(actual: int, expected: int, body: dict[str, Any]) -> None:
    if actual != expected:
        raise SmokeFailure(f"Expected HTTP {expected}, received {actual}: {body}")


def check_health(orchestrator_url: str, timeout: int) -> dict[str, Any]:
    status, body = request_json(
        "GET",
        f"{orchestrator_url}/health",
        timeout=timeout,
    )
    expect_status(status, 200, body)
    if body.get("status") != "ok":
        raise SmokeFailure(f"Unexpected health response: {body}")
    return {"http_status": status, "service_status": body["status"]}


def check_webui(webui_url: str, timeout: int) -> dict[str, Any]:
    cookies = http.cookiejar.CookieJar()
    opener = build_opener(HTTPCookieProcessor(cookies))
    with opener.open(webui_url, timeout=timeout) as response:
        page = response.read().decode("utf-8")
        if response.status != 200 or "Banking Agent Workflow" not in page:
            raise SmokeFailure(f"Unexpected web UI response: HTTP {response.status}")

    token_match = ANTIFORGERY_PATTERN.search(page)
    if token_match is None:
        raise SmokeFailure("Web UI did not render an antiforgery token.")

    form = urlencode(
        {
            "Input.UserMessage": "Why is this card transaction pending?",
            "__RequestVerificationToken": token_match.group(1),
        }
    ).encode("utf-8")
    request = Request(
        webui_url,
        data=form,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        method="POST",
    )
    with opener.open(request, timeout=timeout) as response:
        submitted_page = response.read().decode("utf-8")
        if response.status != 200:
            raise SmokeFailure(f"Web UI form returned HTTP {response.status}.")
        if "Workflow submitted successfully." not in submitted_page:
            raise SmokeFailure("Web UI did not display a successful workflow submission.")

    return {
        "http_status": 200,
        "form_submission": "successful",
        "antiforgery_cookie_count": len(cookies),
    }


def check_agents(foundry_endpoint: str, timeout: int) -> dict[str, Any]:
    token = azure_token("https://ai.azure.com/.default")
    statuses: dict[str, str] = {}
    versions: dict[str, str] = {}

    for agent in EXPECTED_AGENTS:
        status, body = request_json(
            "GET",
            f"{foundry_endpoint}/agents/{agent}/versions?api-version=v1",
            token=token,
            timeout=timeout,
        )
        expect_status(status, 200, body)
        items = body.get("data") or body.get("value") or body.get("items") or []
        ready_versions = [
            item
            for item in items
            if str(item.get("status", "")).lower() in READY_AGENT_STATUSES
        ]
        if not ready_versions:
            raise SmokeFailure(f"{agent} has no active or running version: {body}")

        latest = ready_versions[0]
        statuses[agent] = str(latest["status"]).lower()
        versions[agent] = str(latest["version"])

    return {"statuses": statuses, "versions": versions}


def start_workflow(
    orchestrator_url: str,
    message: str,
    expected_status: str,
    timeout: int,
    token: str | None,
) -> dict[str, Any]:
    status, body = request_json(
        "POST",
        f"{orchestrator_url}/api/v1/workflows",
        body={"userMessage": message},
        token=token,
        timeout=timeout,
    )
    expect_status(status, 200, body)
    if body.get("status") != expected_status:
        raise SmokeFailure(
            f"Expected workflow status {expected_status}, received {body.get('status')}: {body}"
        )
    if not body.get("workflowId") or not body.get("traceId"):
        raise SmokeFailure(f"Workflow response is missing identifiers: {body}")
    return body


def check_workflows(
    orchestrator_url: str,
    timeout: int,
    token: str | None,
) -> dict[str, Any]:
    scenarios = (
        (
            "transaction-information",
            "Why is this card transaction pending?",
            "Completed",
        ),
        (
            "suspicious-information",
            "This transaction is not mine. Explain what I should review.",
            "Completed",
        ),
        (
            "suspicious-action",
            "Freeze my card; this transaction is not mine.",
            "WaitingForApproval",
        ),
        (
            "dispute",
            "Dispute this charge.",
            "WaitingForApproval",
        ),
    )
    results: dict[str, dict[str, Any]] = {}

    for name, message, expected_status in scenarios:
        results[name] = start_workflow(
            orchestrator_url,
            message,
            expected_status,
            timeout,
            token,
        )

    dispute = results["dispute"]
    approval_status, approval = request_json(
        "POST",
        f"{orchestrator_url}/api/v1/workflows/{dispute['workflowId']}/approval",
        body={
            "decision": "approve",
            "reason": "MVP smoke-test approval",
        },
        token=token,
        timeout=timeout,
    )
    expect_status(approval_status, 200, approval)
    if approval.get("status") != "Completed":
        raise SmokeFailure(f"Approved dispute did not complete: {approval}")

    return {
        "scenarios": {
            name: {
                "workflow_id": result["workflowId"],
                "trace_id": result["traceId"],
                "status": result["status"],
            }
            for name, result in results.items()
        },
        "approval": {
            "workflow_id": approval["workflowId"],
            "status": approval["status"],
        },
    }


def check_authentication_baseline(
    orchestrator_url: str,
    timeout: int,
    authentication_expected: bool,
) -> dict[str, Any]:
    status, body = request_json(
        "POST",
        f"{orchestrator_url}/api/v1/workflows/00000000-0000-0000-0000-000000000001/approval",
        body={"decision": "approve", "reason": "Authentication baseline probe"},
        timeout=timeout,
    )
    if authentication_expected:
        if status != 401:
            raise SmokeFailure(
                f"Expected anonymous workflow request to return 401, received {status}: {body}"
            )
    elif status == 401:
        raise SmokeFailure(
            "Anonymous workflow request returned 401 but no ORCHESTRATOR_TOKEN_SCOPE was configured."
        )

    return {
        "anonymous_http_status": status,
        "authentication_required": status == 401,
    }


def parse_args() -> argparse.Namespace:
    parser = argparse.ArgumentParser(description="Run the deployed banking-agent MVP smoke checks.")
    parser.add_argument(
        "--timeout",
        type=int,
        default=int(os.environ.get("SMOKE_TIMEOUT_SECONDS", "120")),
        help="Per-request timeout in seconds.",
    )
    parser.add_argument(
        "--output",
        type=Path,
        help="Optional path for the JSON evidence file.",
    )
    return parser.parse_args()


def main() -> int:
    args = parse_args()
    orchestrator_url = setting("ORCHESTRATOR_URL", "apps", "ORCHESTRATOR_URL").rstrip("/")
    webui_url = setting("WEBUI_URL", "apps", "WEBUI_URL").rstrip("/")
    foundry_endpoint = setting(
        "FOUNDRY_PROJECT_ENDPOINT",
        "infrastructure",
        "FOUNDRY_PROJECT_ENDPOINT",
    ).rstrip("/")
    orchestrator_scope = os.environ.get("ORCHESTRATOR_TOKEN_SCOPE", "").strip()
    orchestrator_token = azure_token(orchestrator_scope) if orchestrator_scope else None

    checks = [
        run_check("orchestrator-health", lambda: check_health(orchestrator_url, args.timeout)),
        run_check("webui-form", lambda: check_webui(webui_url, args.timeout)),
        run_check("foundry-hosted-agents", lambda: check_agents(foundry_endpoint, args.timeout)),
        run_check(
            "orchestrator-authentication-baseline",
            lambda: check_authentication_baseline(
                orchestrator_url,
                args.timeout,
                bool(orchestrator_scope),
            ),
        ),
        run_check(
            "workflow-routing-and-approval",
            lambda: check_workflows(
                orchestrator_url,
                args.timeout,
                orchestrator_token,
            ),
        ),
    ]

    evidence = {
        "status": "passed" if all(check.passed for check in checks) else "failed",
        "orchestrator_url": orchestrator_url,
        "webui_url": webui_url,
        "foundry_project_endpoint": foundry_endpoint,
        "checks": [asdict(check) for check in checks],
    }
    rendered = json.dumps(evidence, indent=2, sort_keys=True)
    print(rendered)

    if args.output:
        args.output.parent.mkdir(parents=True, exist_ok=True)
        args.output.write_text(f"{rendered}\n", encoding="utf-8")

    return 0 if evidence["status"] == "passed" else 1


if __name__ == "__main__":
    try:
        sys.exit(main())
    except (SmokeFailure, subprocess.CalledProcessError, URLError) as error:
        print(f"MVP smoke test setup failed: {error}", file=sys.stderr)
        sys.exit(1)
