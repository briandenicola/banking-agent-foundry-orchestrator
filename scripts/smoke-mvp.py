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
from urllib.parse import quote, urlencode, urlparse
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
WORKFLOW_STATUS_PATTERN = re.compile(
    r'<strong>Status:</strong>\s*(?:<span[^>]*>)?([^<]+)'
)

# States at which polling should stop. Never synthesise a terminal state from
# a timeout — surface the real server-reported status instead.
TERMINAL_STATES: frozenset[str] = frozenset({"Completed", "Failed", "Rejected", "WaitingForApproval"})

# Async-poll defaults (all configurable via CLI --poll-timeout and the per-request --timeout).
DEFAULT_POLL_TIMEOUT_SECONDS = 90
DEFAULT_POLL_INITIAL_INTERVAL = 1.0   # seconds before first retry
DEFAULT_POLL_MAX_INTERVAL = 10.0      # maximum sleep between retries
DEFAULT_POLL_BACKOFF_FACTOR = 2.0     # exponential multiplier


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
    endpoints = {
        "liveness": f"{orchestrator_url}/health/live",
        "readiness": f"{orchestrator_url}/health/ready",
    }
    statuses: dict[str, int] = {}
    for name, url in endpoints.items():
        request = Request(
            url,
            headers={"User-Agent": "banking-agent-mvp-smoke/1.0"},
            method="GET",
        )
        try:
            with urlopen(request, timeout=timeout) as response:
                response.read()
                statuses[name] = response.status
        except HTTPError as error:
            body = error.read().decode("utf-8", errors="replace")
            raise SmokeFailure(
                f"Orchestrator {name} returned HTTP {error.code}: {body}"
            ) from error
    return statuses


def check_webui_readiness(webui_url: str, timeout: int) -> dict[str, Any]:
    request = Request(
        f"{webui_url}/health/ready",
        headers={"User-Agent": "banking-agent-mvp-smoke/1.0"},
        method="GET",
    )
    try:
        with urlopen(request, timeout=timeout) as response:
            response.read()
            return {"readiness": response.status}
    except HTTPError as error:
        body = error.read().decode("utf-8", errors="replace")
        raise SmokeFailure(
            f"Web UI readiness returned HTTP {error.code}: {body}"
        ) from error


def check_container_app_revisions(
    resource_group: str,
    app_urls: tuple[str, ...],
) -> dict[str, Any]:
    revisions: dict[str, Any] = {}
    for app_url in app_urls:
        hostname = urlparse(app_url).hostname
        if not hostname:
            raise SmokeFailure(f"Unable to derive Container App name from {app_url}.")
        app_name = hostname.split(".", 1)[0]
        result = subprocess.run(
            [
                "az",
                "containerapp",
                "show",
                "--name",
                app_name,
                "--resource-group",
                resource_group,
                "--query",
                "{latestRevisionName:properties.latestRevisionName,"
                "latestReadyRevisionName:properties.latestReadyRevisionName,"
                "runningStatus:properties.runningStatus}",
                "--output",
                "json",
            ],
            check=True,
            capture_output=True,
            text=True,
        )
        revision = json.loads(result.stdout)
        if (
            revision.get("latestRevisionName")
            != revision.get("latestReadyRevisionName")
            or revision.get("runningStatus") != "Running"
        ):
            raise SmokeFailure(f"Container App {app_name} is not ready: {revision}")
        revisions[app_name] = revision
    return revisions


def check_webui(webui_url: str, timeout: int, poll_timeout: int) -> dict[str, Any]:
    cookies = http.cookiejar.CookieJar()
    opener = build_opener(HTTPCookieProcessor(cookies))
    workflow, _ = submit_webui_workflow(
        opener,
        webui_url,
        "Why is this card transaction pending?",
        timeout,
    )
    workflow = poll_webui_workflow(
        opener,
        webui_url,
        workflow["workflowId"],
        timeout,
        poll_timeout,
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
            _SUBMIT_SUCCESS_PHRASES = (
                "Workflow submitted. Processing started.",
                "Workflow submitted with supporting evidence. Processing started.",
                "Workflow submitted successfully",
                "accepted for processing",
                "Workflow accepted",
            )
            if not any(phrase in submitted_page for phrase in _SUBMIT_SUCCESS_PHRASES):
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


def poll_webui_workflow(
    opener,
    webui_url: str,
    workflow_id: str,
    request_timeout: int,
    poll_timeout: int,
) -> dict[str, Any]:
    deadline = time.monotonic() + poll_timeout
    interval = DEFAULT_POLL_INITIAL_INTERVAL
    latest: dict[str, Any] = {}

    while time.monotonic() < deadline:
        poll_url = (
            f"{webui_url}?handler=Poll&workflowId={quote(workflow_id, safe='')}"
        )
        try:
            with opener.open(poll_url, timeout=request_timeout) as response:
                latest = json.loads(response.read().decode("utf-8"))
        except (HTTPError, URLError, json.JSONDecodeError) as error:
            raise SmokeFailure(
                f"Web UI polling failed for workflow {workflow_id}: {error}"
            ) from error

        status = str(latest.get("status", ""))
        if status in TERMINAL_STATES:
            return latest

        time.sleep(interval)
        interval = min(
            interval * DEFAULT_POLL_BACKOFF_FACTOR,
            DEFAULT_POLL_MAX_INTERVAL,
        )

    raise SmokeFailure(
        f"Web UI workflow {workflow_id} did not reach a terminal state within "
        f"{poll_timeout}s. Last status: {latest.get('status', 'unknown')!r}."
    )


def load_webui_workflow(opener, webui_url: str, workflow_id: str, timeout: int) -> str:
    page_url = f"{webui_url}?workflowId={quote(workflow_id, safe='')}"
    with opener.open(page_url, timeout=timeout) as response:
        return response.read().decode("utf-8")


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
    timeout: int,
    token: str | None,
) -> dict[str, Any]:
    """POST /api/v1/workflows and require 202 Accepted (async contract).

    Returns the 202 response body ``{workflowId, traceId, status, message}``.
    Does not wait for execution — callers must follow up with ``poll_workflow``.
    """
    status, body = request_json(
        "POST",
        f"{orchestrator_url}/api/v1/workflows",
        body={"userMessage": message},
        token=token,
        timeout=timeout,
    )
    expect_status(status, 202, body)
    if not body.get("workflowId") or not body.get("traceId"):
        raise SmokeFailure(f"202 response is missing workflowId or traceId: {body}")
    return body


def poll_workflow(
    orchestrator_url: str,
    workflow_id: str,
    request_timeout: int,
    poll_timeout: int,
    token: str | None,
) -> dict[str, Any]:
    """Poll GET /api/v1/workflows/{id} with exponential backoff until a terminal state.

    Terminal states: Completed, Failed, Rejected, WaitingForApproval.
    Never synthesises a success state — the caller receives the exact
    server-reported status.  Raises SmokeFailure with timeline details on
    timeout or unexpected HTTP errors.
    """
    interval = DEFAULT_POLL_INITIAL_INTERVAL
    deadline = time.monotonic() + poll_timeout
    attempts = 0
    last_body: dict[str, Any] = {}

    while True:
        attempts += 1
        http_status, body = request_json(
            "GET",
            f"{orchestrator_url}/api/v1/workflows/{workflow_id}",
            token=token,
            timeout=request_timeout,
        )
        last_body = body
        if http_status == 200:
            current_status = body.get("status", "")
            if current_status in TERMINAL_STATES:
                return {
                    "workflow_id": workflow_id,
                    "status": current_status,
                    "poll_attempts": attempts,
                    "events": body.get("events", []),
                    "http_status": http_status,
                }
        elif http_status not in (200,):
            raise SmokeFailure(
                f"Unexpected HTTP {http_status} while polling workflow {workflow_id}: {body}"
            )

        remaining = deadline - time.monotonic()
        if remaining <= 0:
            break
        time.sleep(min(interval, remaining))
        interval = min(interval * DEFAULT_POLL_BACKOFF_FACTOR, DEFAULT_POLL_MAX_INTERVAL)

    # Timed out — surface diagnostic details without synthesising a terminal state.
    timeline = last_body.get("events") or last_body.get("auditTrail") or []
    raise SmokeFailure(
        f"Workflow {workflow_id} did not reach a terminal state within {poll_timeout}s "
        f"after {attempts} poll attempt(s). "
        f"Last status: {last_body.get('status')!r}. "
        f"Timeline ({len(timeline)} event(s)): "
        + (json.dumps(timeline[-5:]) if timeline else "none")
    )


def check_workflows(
    orchestrator_url: str,
    timeout: int,
    poll_timeout: int,
    token: str | None,
) -> dict[str, Any]:
    """Exercise all four workflow routing scenarios via the direct orchestrator API.

    Each scenario follows the async contract: POST → 202 Accepted → poll until
    terminal state.  The expected terminal state for each scenario is verified
    against the real server-reported status; success is never synthesised.
    """
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
            # Step 1: POST → expect 202 with workflowId; execution is async.
            accepted = start_workflow(orchestrator_url, message, timeout, token)
            workflow_id = accepted["workflowId"]
            # Step 2: Poll until a real terminal state is reached (never synthesise).
            polled = poll_workflow(orchestrator_url, workflow_id, timeout, poll_timeout, token)
            if polled["status"] != expected_status:
                raise SmokeFailure(
                    f"Expected terminal status {expected_status!r}, received {polled['status']!r}. "
                    f"Timeline: {json.dumps(polled.get('events', [])[-5:])}"
                )
            polled["agent_execution_modes"] = require_live_model_execution(
                polled.get("events", []),
                name,
            )
            results[name] = {**accepted, **polled}
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

    # Verify GET /api/v1/workflows/{id} returns persisted state.
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
                "poll_attempts": result.get("poll_attempts"),
                "agent_execution_modes": result["agent_execution_modes"],
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


def check_workflows_via_webui(webui_url: str, timeout: int, poll_timeout: int) -> dict[str, Any]:
    """Exercise all four workflow scenarios via the Web UI (managed-identity path).

    After each form POST the Web UI reflects the initial status (Draft under
    the async contract).  Because no orchestrator API token is available in
    this path, polling uses a bounded sleep before the approval step.  Status
    verification for non-approval scenarios is recorded as async-pending rather
    than failing, since the UI's JS polling is not observable here.
    """
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
        initial_status = workflow["status"]
        if initial_status not in TERMINAL_STATES and initial_status not in {
            "Draft",
            "Recovering",
        }:
            raise SmokeFailure(
                f"Scenario {name} returned unexpected initial status {initial_status!r}."
            )
        workflow = poll_webui_workflow(
            opener,
            webui_url,
            workflow["workflowId"],
            timeout,
            poll_timeout,
        )
        if workflow["status"] != expected_status:
            raise SmokeFailure(
                f"Scenario {name} expected {expected_status!r}, "
                f"received {workflow['status']!r}."
            )
        workflow["agentExecutionModes"] = require_live_model_execution(
            workflow.get("events", []),
            name,
        )
        workflow["initialStatus"] = initial_status
        results[name] = workflow
        if evidence is not None and evidence[0] not in page:
            raise SmokeFailure("Uploaded dispute evidence was not displayed by the Web UI.")

    dispute = results["dispute"]
    approval_page = load_webui_workflow(
        opener,
        webui_url,
        dispute["workflowId"],
        timeout,
    )
    approval = approve_webui_workflow(
        opener,
        webui_url,
        dispute["workflowId"],
        approval_page,
        timeout,
    )
    if approval["status"] != "Completed":
        raise SmokeFailure(f"Approved dispute did not complete: {approval}")

    return {
        "transport": "webui-managed-identity",
        "async_status_note": (
            "Initial UI status follows the async contract; final status was verified "
            "through the Web UI polling endpoint."
        ),
        "scenarios": {
            name: {
                "workflow_id": result["workflowId"],
                "trace_id": result["traceId"],
                "initial_status": result["initialStatus"],
                "final_status": result["status"],
                "agent_execution_modes": result["agentExecutionModes"],
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


def require_live_model_execution(
    events: list[dict[str, Any]],
    scenario_name: str,
) -> list[str]:
    required_event_types = {"workflow.plan", "mcp.invoked"}
    execution_modes: dict[str, str] = {}
    for event in events:
        event_type = event.get("type")
        if event_type not in required_event_types:
            continue
        details = event.get("details")
        if not isinstance(details, str):
            continue
        try:
            parsed = json.loads(details)
        except json.JSONDecodeError:
            continue
        if not isinstance(parsed, dict) or "execution_mode" not in parsed:
            continue
        mode = parsed.get("execution_mode")
        if isinstance(mode, str):
            execution_modes[event_type] = mode

    missing_event_types = required_event_types - execution_modes.keys()
    if missing_event_types:
        raise SmokeFailure(
            f"Scenario {scenario_name} did not record planner and specialist "
            f"execution modes; missing events: {sorted(missing_event_types)}"
        )
    if any(mode != "model" for mode in execution_modes.values()):
        raise SmokeFailure(
            f"Scenario {scenario_name} used non-model agent execution: {execution_modes}"
        )
    return [execution_modes["workflow.plan"], execution_modes["mcp.invoked"]]


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
        "--poll-timeout",
        type=int,
        default=int(os.environ.get("SMOKE_POLL_TIMEOUT_SECONDS", str(DEFAULT_POLL_TIMEOUT_SECONDS))),
        help=(
            "Maximum seconds to poll for a terminal workflow state after 202 Accepted "
            f"(default: {DEFAULT_POLL_TIMEOUT_SECONDS}; env: SMOKE_POLL_TIMEOUT_SECONDS)."
        ),
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
        run_check("webui-readiness", lambda: check_webui_readiness(webui_url, args.timeout)),
        run_check(
            "container-app-revisions",
            lambda: check_container_app_revisions(
                resource_group,
                (orchestrator_url, webui_url),
            ),
        ),
        run_check(
            "webui-form",
            lambda: check_webui(webui_url, args.timeout, args.poll_timeout),
        ),
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
                    args.poll_timeout,
                    orchestrator_token,
                )
                if orchestrator_token
                else check_workflows_via_webui(webui_url, args.timeout, args.poll_timeout)
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
        output = parse_args().output
        if output:
            failure = json.dumps(
                {
                    "status": "failed",
                    "checks": [],
                    "setup_error": str(error),
                },
                indent=2,
                sort_keys=True,
            )
            output.parent.mkdir(parents=True, exist_ok=True)
            output.write_text(f"{failure}\n", encoding="utf-8")
        sys.exit(1)
