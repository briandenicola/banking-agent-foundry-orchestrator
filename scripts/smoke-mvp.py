#!/usr/bin/env python3
from __future__ import annotations

import argparse
import http.cookiejar
import html
import json
import os
import re
import subprocess
import sys
import time
import uuid
from dataclasses import asdict, dataclass
from datetime import datetime, timezone
from pathlib import Path
from typing import Any
from urllib.error import HTTPError, URLError
from urllib.parse import urlencode, urlparse
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
WORKFLOW_ID_PATTERN = re.compile(
    r"<strong>Workflow ID:</strong>\s*([0-9a-fA-F-]{36})"
)
TRACE_ID_PATTERN = re.compile(r"<strong>Trace ID:</strong>\s*([^<]+)")
WORKFLOW_STATUS_PATTERN = re.compile(r"<strong>Status:</strong>\s*([^<]+)")


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


def optional_setting(environment_name: str, terraform_directory: str, output_name: str) -> str:
    """Like setting() but returns an empty string if the Terraform output does not exist.

    Use for outputs that are only present in certain deployment configurations
    (e.g., ORCHESTRATOR_TOKEN_SCOPE, which is only available after Lumen's Entra workstream).
    """
    configured = os.environ.get(environment_name, "").strip()
    if configured:
        return configured
    try:
        return terraform_output(terraform_directory, output_name)
    except subprocess.CalledProcessError:
        return ""


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


def collect_container_app_logs(
    resource_group: str,
    app_urls: tuple[str, ...],
    started_at: datetime,
) -> dict[str, Any]:
    app_names = tuple(
        hostname.split(".", 1)[0]
        for app_url in app_urls
        if (hostname := urlparse(app_url).hostname)
    )
    if not app_names:
        return {"error": "Unable to derive Container App names from deployed URLs."}

    try:
        environment_id = subprocess.run(
            [
                "az",
                "containerapp",
                "show",
                "--name",
                app_names[0],
                "--resource-group",
                resource_group,
                "--query",
                "properties.environmentId",
                "--output",
                "tsv",
            ],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        workspace_id = subprocess.run(
            [
                "az",
                "resource",
                "show",
                "--ids",
                environment_id,
                "--query",
                "properties.appLogsConfiguration.logAnalyticsConfiguration.customerId",
                "--output",
                "tsv",
            ],
            check=True,
            capture_output=True,
            text=True,
        ).stdout.strip()
        quoted_names = ", ".join(f"'{name}'" for name in app_names)
        started_at_utc = started_at.strftime("%Y-%m-%dT%H:%M:%SZ")
        query = (
            "ContainerAppConsoleLogs_CL "
            f"| where TimeGenerated >= datetime({started_at_utc}) "
            f"| where ContainerAppName_s in ({quoted_names}) "
            '| where Log_s matches regex @"(?i)(fail|error|exception|denied|unauthorized|permission|completed routing|routing policy)" '
            "| project TimeGenerated, ContainerAppName_s, RevisionName_s, Log_s "
            "| order by TimeGenerated asc "
            "| take 100"
        )
        result = subprocess.run(
            [
                "az",
                "monitor",
                "log-analytics",
                "query",
                "--workspace",
                workspace_id,
                "--analytics-query",
                query,
                "--output",
                "json",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        return {"entries": json.loads(result.stdout)}
    except (json.JSONDecodeError, subprocess.CalledProcessError) as error:
        details = error.stderr.strip() if isinstance(error, subprocess.CalledProcessError) else str(error)
        return {"error": details or "Unable to query Container Apps logs."}


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
    workflow, _ = submit_webui_workflow(
        opener,
        webui_url,
        "Why is this card transaction pending?",
        timeout,
    )
    if workflow["status"] != "Completed":
        raise SmokeFailure(f"Web UI workflow did not complete: {workflow}")

    return {
        "http_status": 200,
        "form_submission": "successful",
        "antiforgery_cookie_count": len(cookies),
        "workflow_id": workflow["workflowId"],
    }


def submit_webui_workflow(
    opener,
    webui_url: str,
    message: str,
    timeout: int,
    evidence: tuple[str, str, bytes] | None = None,
) -> tuple[dict[str, str], str]:
    with opener.open(webui_url, timeout=timeout) as response:
        page = response.read().decode("utf-8")
        if response.status != 200 or "Northstar Banking" not in page:
            raise SmokeFailure(f"Unexpected web UI response: HTTP {response.status}")

    token_match = ANTIFORGERY_PATTERN.search(page)
    if token_match is None:
        raise SmokeFailure("Web UI did not render an antiforgery token.")

    fields = {
            "Input.UserMessage": message,
            "__RequestVerificationToken": token_match.group(1),
    }
    if evidence is None:
        form = urlencode(fields).encode("utf-8")
        content_type = "application/x-www-form-urlencoded"
    else:
        boundary = f"banking-agent-smoke-{uuid.uuid4().hex}"
        form = encode_multipart(
            fields,
            [("Input.EvidenceFiles", evidence[0], evidence[1], evidence[2])],
            boundary,
        )
        content_type = f"multipart/form-data; boundary={boundary}"

    request = Request(
        webui_url,
        data=form,
        headers={"Content-Type": content_type},
        method="POST",
    )
    try:
        with opener.open(request, timeout=timeout) as response:
            submitted_page = response.read().decode("utf-8")
            if response.status != 200:
                raise SmokeFailure(f"Web UI form returned HTTP {response.status}.")
            if "Workflow submitted successfully" not in submitted_page:
                raise SmokeFailure("Web UI did not display a successful workflow submission.")
    except HTTPError as error:
        response_body = error.read().decode("utf-8", errors="replace")
        raise SmokeFailure(
            f"Web UI form returned HTTP {error.code}: {response_body[:2000]}"
        ) from error

    return parse_webui_workflow(submitted_page), submitted_page


def encode_multipart(
    fields: dict[str, str],
    files: list[tuple[str, str, str, bytes]],
    boundary: str,
) -> bytes:
    body = bytearray()
    for name, value in fields.items():
        body.extend(f"--{boundary}\r\n".encode())
        body.extend(f'Content-Disposition: form-data; name="{name}"\r\n\r\n'.encode())
        body.extend(value.encode())
        body.extend(b"\r\n")

    for field_name, file_name, content_type, content in files:
        body.extend(f"--{boundary}\r\n".encode())
        body.extend(
            f'Content-Disposition: form-data; name="{field_name}"; filename="{file_name}"\r\n'.encode()
        )
        body.extend(f"Content-Type: {content_type}\r\n\r\n".encode())
        body.extend(content)
        body.extend(b"\r\n")

    body.extend(f"--{boundary}--\r\n".encode())
    return bytes(body)


def parse_webui_workflow(page: str) -> dict[str, str]:
    workflow_id = WORKFLOW_ID_PATTERN.search(page)
    trace_id = TRACE_ID_PATTERN.search(page)
    status = WORKFLOW_STATUS_PATTERN.search(page)
    if workflow_id is None or trace_id is None or status is None:
        raise SmokeFailure("Web UI workflow response is missing identifiers or status.")

    return {
        "workflowId": workflow_id.group(1),
        "traceId": html.unescape(trace_id.group(1)).strip(),
        "status": html.unescape(status.group(1)).strip(),
    }


def approve_webui_workflow(
    opener,
    webui_url: str,
    workflow_id: str,
    page: str,
    timeout: int,
) -> dict[str, str]:
    token_match = ANTIFORGERY_PATTERN.search(page)
    if token_match is None:
        raise SmokeFailure("Web UI approval form did not include an antiforgery token.")

    form = urlencode(
        {
            "workflowId": workflow_id,
            "ApprovalInput.Decision": "approve",
            "ApprovalInput.Reason": "MVP smoke-test approval",
            "__RequestVerificationToken": token_match.group(1),
        }
    ).encode("utf-8")
    request = Request(
        f"{webui_url}?handler=Approve",
        data=form,
        headers={"Content-Type": "application/x-www-form-urlencoded"},
        method="POST",
    )
    try:
        with opener.open(request, timeout=timeout) as response:
            approved_page = response.read().decode("utf-8")
            if response.status != 200:
                raise SmokeFailure(f"Web UI approval returned HTTP {response.status}.")
            if "Approval recorded successfully." not in approved_page:
                raise SmokeFailure("Web UI did not display a successful approval.")
    except HTTPError as error:
        response_body = error.read().decode("utf-8", errors="replace")
        raise SmokeFailure(
            f"Web UI approval returned HTTP {error.code}: {response_body[:2000]}"
        ) from error

    return parse_webui_workflow(approved_page)


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
        try:
            results[name] = start_workflow(
                orchestrator_url,
                message,
                expected_status,
                timeout,
                token,
            )
        except SmokeFailure as error:
            raise SmokeFailure(f"Scenario {name} failed: {error}") from error

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

    # Verify GET /api/v1/workflows/{id} returns persisted state (post-Theo).
    # Skipped gracefully on pre-Theo deployments where the endpoint returns 404/501.
    transaction_workflow = results["transaction-information"]
    get_state = check_workflow_get_state(
        orchestrator_url,
        transaction_workflow["workflowId"],
        transaction_workflow["status"],
        timeout,
        token,
    )

    approved_get_state = check_workflow_get_state(
        orchestrator_url,
        approval["workflowId"],
        approval["status"],
        timeout,
        token,
    )

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
        "workflow_get_state": get_state,
        "approved_workflow_get_state": approved_get_state,
    }


def check_workflows_via_webui(webui_url: str, timeout: int) -> dict[str, Any]:
    scenarios = (
        ("transaction-information", "Why is this card transaction pending?", "Completed"),
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
        ("dispute", "Dispute this charge.", "WaitingForApproval"),
    )
    cookies = http.cookiejar.CookieJar()
    opener = build_opener(HTTPCookieProcessor(cookies))
    results: dict[str, dict[str, str]] = {}
    pages: dict[str, str] = {}

    for name, message, expected_status in scenarios:
        evidence = (
            "smoke-evidence.png",
            "image/png",
            b"\x89PNG\r\n\x1a\nsmoke",
        ) if name == "dispute" else None
        workflow, page = submit_webui_workflow(
            opener,
            webui_url,
            message,
            timeout,
            evidence,
        )
        if workflow["status"] != expected_status:
            raise SmokeFailure(
                f"Scenario {name} expected {expected_status}, received {workflow['status']}."
            )
        results[name] = workflow
        pages[name] = page
        if evidence is not None and evidence[0] not in page:
            raise SmokeFailure("Uploaded dispute evidence was not displayed by the Web UI.")

    dispute = results["dispute"]
    approval = approve_webui_workflow(
        opener,
        webui_url,
        dispute["workflowId"],
        pages["dispute"],
        timeout,
    )
    if approval["status"] != "Completed":
        raise SmokeFailure(f"Approved dispute did not complete: {approval}")

    return {
        "transport": "webui-managed-identity",
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
        "workflow_get_state": {
            "skipped": True,
            "reason": "Direct API token unavailable; workflow lookup remains covered by API tests.",
        },
    }


def check_workflow_get_state(
    orchestrator_url: str,
    workflow_id: str,
    expected_status: str,
    timeout: int,
    token: str | None,
) -> dict[str, Any]:
    """GET /api/v1/workflows/{id} and verify the persisted state matches the expected status.

    Returns a skipped result when the endpoint is not yet implemented (HTTP 404/501) so
    that the smoke run does not fail on pre-Theo deployments.
    """
    status, body = request_json(
        "GET",
        f"{orchestrator_url}/api/v1/workflows/{workflow_id}",
        token=token,
        timeout=timeout,
    )
    if status in (404, 501):
        # GET endpoint not yet deployed (pre-Theo persistence workstream); skip gracefully.
        return {"skipped": True, "reason": f"GET endpoint returned {status} (not yet available)"}
    expect_status(status, 200, body)
    actual_status = body.get("status")
    if actual_status != expected_status:
        raise SmokeFailure(
            f"GET /api/v1/workflows/{workflow_id} returned status {actual_status!r}, "
            f"expected {expected_status!r}: {body}"
        )
    return {
        "http_status": status,
        "workflow_id": workflow_id,
        "status": actual_status,
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
    started_at = datetime.now(timezone.utc)
    orchestrator_url = setting("ORCHESTRATOR_URL", "apps", "ORCHESTRATOR_URL").rstrip("/")
    webui_url = setting("WEBUI_URL", "apps", "WEBUI_URL").rstrip("/")
    resource_group = setting(
        "APPS_RESOURCE_GROUP_NAME",
        "apps",
        "APPS_RESOURCE_GROUP_NAME",
    )
    foundry_endpoint = setting(
        "FOUNDRY_PROJECT_ENDPOINT",
        "infrastructure",
        "FOUNDRY_PROJECT_ENDPOINT",
    ).rstrip("/")
    orchestrator_scope = optional_setting("ORCHESTRATOR_TOKEN_SCOPE", "apps", "ORCHESTRATOR_TOKEN_SCOPE")
    orchestrator_token = os.environ.get("ORCHESTRATOR_ACCESS_TOKEN", "").strip() or None

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
            lambda: (
                check_workflows(
                    orchestrator_url,
                    args.timeout,
                    orchestrator_token,
                )
                if orchestrator_token
                else check_workflows_via_webui(webui_url, args.timeout)
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
    if evidence["status"] == "failed":
        evidence["diagnostics"] = {
            "container_app_logs": collect_container_app_logs(
                resource_group,
                (webui_url, orchestrator_url),
                started_at,
            )
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
