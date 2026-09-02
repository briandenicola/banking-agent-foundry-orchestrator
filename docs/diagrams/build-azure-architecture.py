#!/usr/bin/env python3
"""Generate the Azure architecture Excalidraw diagram from the icon set.

The diagram is regenerated rather than hand-edited so it can be kept in step
with the Terraform stacks. Run it after changing `infrastructure/` or `apps/`:

    python docs/diagrams/build-azure-architecture.py

It needs the official Microsoft Azure architecture icon set, which is not
vendored here. Download and point ICON_ROOT at it:

    curl -sL https://arch-center.azureedge.net/icons/Azure_Public_Service_Icons_V19.zip -o /tmp/azicons.zip
    unzip -q /tmp/azicons.zip -d /tmp/azicons

Icons are embedded in the output as base64 SVG data URLs, so the .excalidraw
file is self-contained and needs no network access to open.
"""

from __future__ import annotations

import base64
import hashlib
import json
import os
import random
import sys
from pathlib import Path

ICON_ROOT = Path(
    os.environ.get("AZURE_ICONS", "/tmp/azicons/Azure_Public_Service_Icons/Icons")
)
OUTPUT = Path(__file__).with_name("azure-architecture.excalidraw")

# Deterministic output: a rebuild with no content change produces no diff.
random.seed(20260902)

ICONS = {
    "browser": "general/10783-icon-service-Browser.svg",
    "users": "identity/10230-icon-service-Users.svg",
    "rg": "general/10007-icon-service-Resource-Groups.svg",
    "cae": "other/02989-icon-service-Container-Apps-Environments.svg",
    "containerapp": "other/02884-icon-service-Worker-Container-App.svg",
    "job": "general/10833-icon-service-Scheduler.svg",
    "acr": "containers/10105-icon-service-Container-Registries.svg",
    "postgres": "databases/10131-icon-service-Azure-Database-PostgreSQL-Server.svg",
    "foundry": "ai + machine learning/03513-icon-service-AI-Studio.svg",
    "model": "ai + machine learning/10162-icon-service-Cognitive-Services.svg",
    "identity": "identity/10227-icon-service-Managed-Identities.svg",
    "appreg": "identity/10232-icon-service-App-Registrations.svg",
    "loganalytics": "analytics/00009-icon-service-Log-Analytics-Workspaces.svg",
    "appinsights": "devops/00012-icon-service-Application-Insights.svg",
    "vnet": "networking/10061-icon-service-Virtual-Networks.svg",
    "subnet": "networking/02742-icon-service-Subnet.svg",
    "dns": "networking/10064-icon-service-DNS-Zones.svg",
}

BLUE = "#0078d4"
DARKBLUE = "#0063b1"
GREEN = "#2f9e44"
ORANGE = "#e8590c"
PURPLE = "#7048e8"
RED = "#e03131"
GREY = "#495057"
INK = "#1e1e1e"
MUTED = "#5f6b7a"

elements: list[dict] = []
files: dict[str, dict] = {}


def _seed() -> int:
    return random.randint(1, 2**31 - 1)


def _base() -> dict:
    return {
        "angle": 0,
        "strokeWidth": 1,
        "strokeStyle": "solid",
        "roughness": 0,
        "opacity": 100,
        "groupIds": [],
        "frameId": None,
        "seed": _seed(),
        "version": 1,
        "versionNonce": _seed(),
        "isDeleted": False,
        "boundElements": None,
        "updated": 1788000000000,
        "link": None,
        "locked": False,
    }


def icon(key: str, x: float, y: float, size: float = 40) -> None:
    path = ICON_ROOT / ICONS[key]
    if not path.exists():
        sys.exit(f"missing icon: {path}\nSet AZURE_ICONS to the extracted icon set.")
    raw = path.read_bytes()
    file_id = hashlib.sha1(ICONS[key].encode()).hexdigest()[:40]
    if file_id not in files:
        files[file_id] = {
            "mimeType": "image/svg+xml",
            "id": file_id,
            "dataURL": "data:image/svg+xml;base64," + base64.b64encode(raw).decode(),
            "created": 1788000000000,
            "lastRetrieved": 1788000000000,
        }
    elements.append(
        {
            **_base(),
            "id": f"img{len(elements)}",
            "type": "image",
            "x": x,
            "y": y,
            "width": size,
            "height": size,
            "strokeColor": "transparent",
            "backgroundColor": "transparent",
            "fillStyle": "solid",
            "fileId": file_id,
            "status": "saved",
            "scale": [1, 1],
            "roundness": None,
        }
    )


def text(
    body: str,
    x: float,
    y: float,
    size: int = 12,
    color: str = INK,
    align: str = "left",
    width: float | None = None,
) -> None:
    lines = body.split("\n")
    w = width if width is not None else max(len(ln) for ln in lines) * size * 0.58
    elements.append(
        {
            **_base(),
            "id": f"txt{len(elements)}",
            "type": "text",
            "x": x,
            "y": y,
            "width": w,
            "height": len(lines) * (size * 1.25),
            "strokeColor": color,
            "backgroundColor": "transparent",
            "fillStyle": "solid",
            "text": body,
            "fontSize": size,
            "fontFamily": 2,
            "textAlign": align,
            "verticalAlign": "top",
            "containerId": None,
            "originalText": body,
            "autoResize": True,
            "lineHeight": 1.25,
            "roundness": None,
        }
    )


def box(
    x: float,
    y: float,
    w: float,
    h: float,
    color: str = BLUE,
    dashed: bool = False,
    bg: str = "transparent",
) -> None:
    elements.append(
        {
            **_base(),
            "id": f"rect{len(elements)}",
            "type": "rectangle",
            "x": x,
            "y": y,
            "width": w,
            "height": h,
            "strokeColor": color,
            "backgroundColor": bg,
            "fillStyle": "solid",
            "strokeStyle": "dashed" if dashed else "solid",
            "roundness": {"type": 3},
        }
    )


def arrow(
    x: float,
    y: float,
    dx: float,
    dy: float,
    color: str = GREY,
    dashed: bool = False,
) -> None:
    elements.append(
        {
            **_base(),
            "id": f"arr{len(elements)}",
            "type": "arrow",
            "x": x,
            "y": y,
            "width": abs(dx),
            "height": abs(dy),
            "strokeColor": color,
            "backgroundColor": "transparent",
            "fillStyle": "solid",
            "strokeStyle": "dashed" if dashed else "solid",
            "points": [[0, 0], [dx, dy]],
            "lastCommittedPoint": None,
            "startBinding": None,
            "endBinding": None,
            "startArrowhead": None,
            "endArrowhead": "arrow",
            "elbowed": False,
            "roundness": {"type": 2},
        }
    )


def polyline(
    x: float,
    y: float,
    pts: list,
    color: str = GREY,
    dashed: bool = False,
) -> None:
    """An elbowed arrow, so a connector can be routed around boxes and labels."""
    xs = [p[0] for p in pts]
    ys = [p[1] for p in pts]
    elements.append(
        {
            **_base(),
            "id": f"arr{len(elements)}",
            "type": "arrow",
            "x": x,
            "y": y,
            "width": max(xs) - min(xs),
            "height": max(ys) - min(ys),
            "strokeColor": color,
            "backgroundColor": "transparent",
            "fillStyle": "solid",
            "strokeStyle": "dashed" if dashed else "solid",
            "points": pts,
            "lastCommittedPoint": None,
            "startBinding": None,
            "endBinding": None,
            "startArrowhead": None,
            "endArrowhead": "arrow",
            "elbowed": False,
            "roundness": {"type": 2},
        }
    )


def node(key: str, x: float, y: float, label: str, size: float = 40) -> None:
    """An icon centred above a label."""
    icon(key, x, y, size)
    text(label, x - 60 + size / 2, y + size + 6, 11, INK, "center", width=120)


# ---------------------------------------------------------------------------
# Header
# ---------------------------------------------------------------------------
text("Banking Agent Foundry Orchestrator — Azure Architecture", 40, 24, 28)
text(
    "Region: swedencentral  |  two Terraform stacks: infrastructure/ (shared) and apps/ (workloads)  "
    "|  Entra ID + managed identity only, no keys or connection-string secrets",
    40,
    64,
    13,
    MUTED,
)

# Legend
box(1230, 92, 700, 156, GREY)
text("Legend", 1244, 100, 14, GREY)
arrow(1250, 132, 40, 0, BLUE)
text("HTTP request flow", 1300, 124, 12)
arrow(1250, 158, 40, 0, GREEN)
text("managed-identity access to a data or model plane", 1300, 150, 12)
arrow(1250, 184, 40, 0, MUTED, dashed=True)
text("deployment-time only (manual jobs, image pull)", 1300, 176, 12)
text(
    "Icons: official Microsoft Azure architecture icon set.  Dashed containers are optional and off by default.",
    1244,
    210,
    11,
    MUTED,
)

# ---------------------------------------------------------------------------
# Public entry
# ---------------------------------------------------------------------------
node("users", 120, 110, "Internet / end users", 46)
arrow(215, 133, 105, 0, BLUE)
text("HTTPS", 240, 112, 11, MUTED)
node("browser", 360, 110, "Browser\n(Razor Pages UI)", 46)

text(
    "The Web UI is the only public surface, and it is unauthenticated:\n"
    "anyone who can reach it can start and approve workflows (issue #40).\n"
    "The orchestrator behind it has no public route at all.",
    620,
    112,
    12,
    RED,
)
text("HTTPS to the public Web UI ingress", 415, 216, 11, MUTED)

# ---------------------------------------------------------------------------
# Infrastructure resource group
# ---------------------------------------------------------------------------
box(40, 260, 1150, 1200, BLUE)
icon("rg", 58, 272, 30)
text("Resource group:  <pet>-<id>-rg      infrastructure/ stack", 96, 278, 18, BLUE)
text(
    "Shared, long-lived platform. `region` is the only Terraform input; every name is derived from a random pet + id.",
    96,
    304,
    11,
    MUTED,
)

# Container Apps environment
box(70, 340, 1090, 520, DARKBLUE)
icon("cae", 88, 352, 28)
text("Container Apps environment   <name>-env", 124, 358, 16, DARKBLUE)
text(
    "Workload profile: Consumption.  Logs flow to the Log Analytics workspace below.",
    124,
    382,
    11,
    MUTED,
)

# Container apps
box(95, 410, 1040, 250, BLUE, bg="transparent")
text("Container apps            apps/ stack", 112, 420, 13, BLUE)

node("containerapp", 175, 460, "webui\nexternal ingress :8080\n1 replica (fixed)", 44)
text("public FQDN", 150, 560, 10, GREEN, "center", width=120)

arrow(255, 482, 130, 0, BLUE)
text("internal HTTP", 275, 462, 10, MUTED)

node(
    "containerapp",
    405,
    460,
    "orchestrator\ninternal ingress only :8080\n1–3 replicas",
    44,
)
text("no public route", 380, 560, 10, RED, "center", width=120)

text(
    "Both apps run the same commit-tagged image set. revision_mode = Single, so a\n"
    "rollback is a Terraform apply with a previous immutable tag.",
    620,
    455,
    11,
    MUTED,
)
text(
    "The Web UI holds ASP.NET Data Protection keys in container-local storage, so\n"
    "replacing its single replica invalidates existing antiforgery cookies.",
    620,
    500,
    11,
    MUTED,
)
text(
    "Orchestrator internal FQDN:\n<name>-orchestrator.internal.<env-domain>",
    620,
    550,
    11,
    MUTED,
)

# Jobs
box(95, 680, 1040, 160, MUTED, dashed=True)
text("Container Apps jobs — manual trigger, deployment-time only", 112, 690, 13, MUTED)

node("job", 175, 726, "database-migrator\nEF Core migrations\n+ runtime grants", 40)
node("job", 405, 726, "agent-deployer\nregisters hosted agents\nwith Foundry", 40)

text(
    "Run through `task app:migrate` and `task app:deploy-hosted-agents`.\n"
    "Neither job runs on a schedule; both are idempotent and safe to repeat.",
    620,
    730,
    11,
    MUTED,
)

# Data
box(70, 900, 545, 250, GREEN)
text("Data", 88, 910, 14, GREEN)
node(
    "postgres",
    110,
    946,
    "PostgreSQL Flexible Server\nv16 · B_Standard_B1ms · 32 GB\ndatabase: banking_agent",
    46,
)
text(
    "Entra ID authentication only —\n"
    "`password_auth_enabled = false`.\n"
    "The migrator identity is the AAD\n"
    "administrator; the orchestrator gets\n"
    "runtime-only table grants.",
    290,
    948,
    11,
    INK,
)
text(
    "Default path: public endpoint with the AllowAzureServices firewall rule, which admits any Azure tenant\n"
    "at the network layer. Set enable_private_networking=true for the VNet path shown at the bottom right.",
    88,
    1084,
    11,
    RED,
)

# Monitoring
box(635, 900, 525, 250, RED)
text("Monitoring", 653, 910, 14, RED)
node("loganalytics", 690, 946, "Log Analytics\nPerGB2018 · 30-day retention", 46)
node("appinsights", 900, 946, "Application Insights\napplication_type = web", 46)
text(
    "OpenTelemetry traces from both apps share W3C trace context. Foundry account\n"
    "diagnostic settings also land in this workspace. See docs/observability.md.",
    653,
    1074,
    11,
    MUTED,
)

# Platform
box(70, 1170, 1090, 260, PURPLE)
text("Platform and identity", 88, 1180, 14, PURPLE)

node("acr", 120, 1216, "Container Registry\nStandard · admin disabled\nanonymous pull disabled", 46)
text(
    "Five images, tagged with the first eight\ncharacters of the commit: orchestrator,\n"
    "webui, hosted-agents, agent-deployer,\ndatabase-migrator.",
    300,
    1218,
    11,
    MUTED,
)

node("identity", 560, 1216, "User-assigned managed identities", 46)
text(
    "orchestrator · webui · agent-deployer   (apps/ stack)\n"
    "database-migrator                       (infrastructure/ stack)",
    700,
    1218,
    11,
    INK,
)
text(
    "Role assignments: AcrPull on the registry · Cognitive Services User\n"
    "on the Foundry account · Foundry Agent Consumer (orchestrator) and\n"
    "Foundry Project Manager (deployer) on the project · the Foundry\n"
    "project identity itself gets AcrPull and Foundry User so it can pull\n"
    "and run agent images.",
    700,
    1252,
    11,
    MUTED,
)

node("appreg", 560, 1336, "Entra app registration", 40)
text(
    "Optional. Creates the `Workflow.Invoke` app role and the api://\n"
    "identifier URI, and assigns the role to the Web UI identity so\n"
    "orchestrator endpoints require a managed-identity bearer token.\n"
    "Disabled here: this tenant forbids the required directory objects,\n"
    "so ENABLE_SERVICE_AUTH=false and ALLOW_INSECURE_SERVICE_AUTH=true —\n"
    "which is why the orchestrator is internal-ingress only.",
    700,
    1336,
    11,
    ORANGE,
)

# ---------------------------------------------------------------------------
# Foundry column
# ---------------------------------------------------------------------------
box(1230, 280, 700, 660, ORANGE)
text("Microsoft Foundry      infrastructure/ stack", 1248, 290, 16, ORANGE)

icon("foundry", 1266, 322, 46)
text("Foundry account   AIServices, S0", 1326, 326, 13)
text("disableLocalAuth = true", 1326, 346, 11, GREEN)
text(
    "Local auth is disabled, so Entra ID is the only way in.\n"
    "allowProjectManagement = true lets the deployer job\n"
    "create and version agents. Reached from the orchestrator\n"
    "over MCP/HTTPS using its user-assigned managed identity.",
    1560,
    322,
    10,
    MUTED,
)

box(1250, 420, 660, 150, ORANGE, dashed=True)
text("Project   <name>-project", 1266, 428, 13, ORANGE)
node("model", 1290, 458, "gpt-5.4-mini\nGlobalStandard · capacity 10", 40)
node("model", 1520, 458, "text-embedding-3-small\nGlobalStandard · capacity 10", 40)
text(
    "The embedding deployment exists only\nto back Foundry memory, and is\nserialized behind the chat deployment\nbecause the API rejects concurrent\nwrites to one account.",
    1660,
    452,
    10,
    MUTED,
)

box(1250, 590, 660, 200, ORANGE, dashed=True)
text("Hosted agents   one shared image, four registrations", 1266, 598, 13, ORANGE)
node("containerapp", 1290, 630, "workflow-planning", 36)
node("containerapp", 1450, 630, "transaction-explanation", 36)
node("containerapp", 1610, 630, "suspicious-activity", 36)
node("containerapp", 1770, 630, "dispute-planning", 36)
text(
    "Python LangGraph graphs behind an MCP boundary: the orchestrator runs `initialize`, discovers with\n"
    "`tools/list`, and invokes with `tools/call`. Each registration gets its own instance identity, which\n"
    "`scripts/deploy-hosted-agents.sh` grants Cognitive Services OpenAI User after the job succeeds.",
    1266,
    724,
    11,
    MUTED,
)

box(1250, 810, 660, 110, ORANGE, dashed=True)
text("Optional Foundry features — off by default", 1266, 818, 13, ORANGE)
text(
    "ENABLE_AGENT_MEMORY   adds a memory store and the `customer-profile` prompt agent (preview API).\n"
    "ENABLE_AGENT_TOOLBOX  adds the `banking-toolbox` toolbox: code interpreter + toolbox search.\n"
    "Both must be supplied on every apply, or Terraform reapplies the false default.",
    1266,
    844,
    11,
    MUTED,
)

# Flows into Foundry
arrow(1194, 470, 40, -80, GREEN)
arrow(1194, 700, 40, -30, MUTED, dashed=True)

# ---------------------------------------------------------------------------
# Apps resource group note
# ---------------------------------------------------------------------------
box(1230, 960, 700, 130, BLUE)
icon("rg", 1248, 972, 26)
text("Resource group:  <pet>-<id>-apps-rg      apps/ stack", 1282, 976, 15, BLUE)
text(
    "Holds the two container apps, the two jobs, three managed identities, and every role assignment.\n"
    "Separating it from the shared group means the whole workload can be destroyed and re-applied\n"
    "without touching Foundry, the registry, the database, or the monitoring workspace.\n"
    "`scripts/guard-stack-alignment.sh` refuses to apply if the two stacks disagree about which\n"
    "environment they target.",
    1248,
    1004,
    11,
    MUTED,
)

# ---------------------------------------------------------------------------
# Optional private networking
# ---------------------------------------------------------------------------
box(1230, 1110, 700, 330, DARKBLUE, dashed=True)
icon("vnet", 1248, 1122, 26)
text("Optional private networking   enable_private_networking = true", 1282, 1126, 15, DARKBLUE)
text("Off by default, and not used in the verified deployment.", 1282, 1150, 11, MUTED)

node("subnet", 1270, 1186, "container-apps\n10.42.0.0/23\ndelegated to\nMicrosoft.App", 40)
node("subnet", 1470, 1186, "postgresql\n10.42.2.0/24\ndelegated to\nflexibleServers", 40)
node("dns", 1690, 1186, "Private DNS zone\nprivatelink.postgres\n.database.azure.com", 40)

text(
    "VNet 10.42.0.0/16. When enabled, the Container Apps environment joins the delegated subnet, the\n"
    "PostgreSQL server moves to a private endpoint with public access disabled, and the broad\n"
    "AllowAzureServices firewall rule is removed. Foundry and the registry stay on public endpoints —\n"
    "there are no private endpoints for them in this stack.",
    1248,
    1330,
    11,
    MUTED,
)

# ---------------------------------------------------------------------------
# Cross-cutting flows
# ---------------------------------------------------------------------------
polyline(360, 212, [[0, 0], [-300, 0], [-300, 270], [-267, 270]], BLUE)

arrow(200, 864, 0, 32, GREEN)
text("EF Core over an Entra ID token — no password anywhere", 218, 868, 11, GREEN)

arrow(760, 864, 0, 32, RED)
text("logs + traces", 778, 868, 11, RED)

data = {
    "type": "excalidraw",
    "version": 2,
    "source": "https://github.com/briandenicola/banking-agent-foundry-orchestrator",
    "elements": elements,
    "appState": {"gridSize": None, "viewBackgroundColor": "#ffffff"},
    "files": files,
}

OUTPUT.write_text(json.dumps(data, indent=2) + "\n")
print(f"wrote {OUTPUT} — {len(elements)} elements, {len(files)} embedded icons")
