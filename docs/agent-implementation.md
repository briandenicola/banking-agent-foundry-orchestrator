# Agent implementation and Foundry runtime

This document traces the current agent code from the C# orchestrator through the
Python LangGraph runtime and Microsoft Foundry deployment. It distinguishes what the
repository implements today from the Agent Framework and MCP target architecture.

## Current architecture in one sentence

The C# `WorkflowService` now runs a small Agent Framework workflow that sequences
planner, routing, and specialist execution before it persists workflow state in
PostgreSQL; the Python agents still do not call each other or access PostgreSQL.
Only the `transaction-explanation` slice currently speaks genuine MCP: the C#
client sends JSON-RPC 2.0 `initialize`, `tools/list`, and `tools/call` messages to
the authenticated Foundry hosted-agent endpoint. `workflow-planning`,
`suspicious-activity`, and `dispute-planning` still use the versioned typed HTTP
envelope and do not claim MCP metadata on the wire.

```mermaid
flowchart LR
    API[Workflow API] --> Service[C# WorkflowService]
    Service --> DB[(PostgreSQL)]
    Service --> Client[FoundryMcpClient]
    Client --> Planner[Foundry: workflow-planning]
    Planner --> Client
    Client --> Service
    Service --> Policy[C# WorkflowRoutingPolicy]
    Service --> Client
    Client --> Specialist[Foundry: selected specialist]
    Specialist --> Client
    Client --> Service
    Service --> DB
```

The repository now includes both an Agent Framework workflow orchestration path and
a hybrid transport boundary: one real MCP tool plus three typed-envelope tools. The
current implementation uses the C# orchestrator to own workflow state, approvals,
and durable persistence while the hosted agents remain specialized reasoning
services. The implementation details in this document are the source of truth for
the current architecture.

## Source map

| Concern | Source |
| --- | --- |
| LangGraph state and topology | [`base.py`](../src/agents/python/app/agents/base.py#L11-L25) |
| Planner instructions | [`planning.py`](../src/agents/python/app/agents/planning.py#L4-L12) |
| Transaction instructions | [`transaction_explanation.py`](../src/agents/python/app/agents/transaction_explanation.py#L4-L11) |
| Suspicious-activity instructions | [`suspicious_activity.py`](../src/agents/python/app/agents/suspicious_activity.py#L4-L11) |
| Dispute instructions | [`dispute.py`](../src/agents/python/app/agents/dispute.py#L4-L11) |
| Agent registry | [`registry.py`](../src/agents/python/app/agents/registry.py#L9-L18) |
| Request/result schemas | [`contracts.py`](../src/agents/python/app/contracts.py#L9-L40) |
| Model and deterministic fallback | [`model.py`](../src/agents/python/app/model.py#L15-L143) |
| Foundry hosted entrypoint | [`hosted.py`](../src/agents/python/app/hosted.py#L20-L67) |
| Shared hosted image | [`Dockerfile.hosted`](../src/agents/python/Dockerfile.hosted) |
| Agent Framework workflow orchestration | [`AgentFrameworkWorkflowOrchestrator`](../src/application/AgentFrameworkWorkflowOrchestrator.cs) |
| C# planner/specialist sequence | [`WorkflowService.ExecuteRoutingAsync`](../src/application/WorkflowService.cs) |
| C# hosted-agent telemetry | [`WorkflowService.InvokeAgentAsync`](../src/application/WorkflowService.cs) |
| C# result validation | [`WorkflowService.TryReadAgentResult`](../src/application/WorkflowService.cs) |
| Foundry MCP and typed-envelope invocation | [`FoundryMcpClient.DiscoverToolsAsync`](../src/infrastructure/FoundryMcpClient.cs) and [`FoundryMcpClient.InvokeAsync`](../src/infrastructure/FoundryMcpClient.cs) |
| Tool-to-agent endpoint map | [`apps/main.tf`](../apps/main.tf#L27-L32) |
| Hosted-agent definitions | [`apps/main.tf`](../apps/main.tf#L34-L55) |
| Deployer Container Apps Job | [`apps/agent-deployer.tf`](../apps/agent-deployer.tf) |
| Foundry registration client | [`deploy.py`](../src/agents/deployer/deploy.py#L28-L289) |
| Foundry and ACR roles | [`apps/roles.tf`](../apps/roles.tf#L19-L54) |
| Post-deploy instance access | [`deploy-hosted-agents.sh`](../scripts/deploy-hosted-agents.sh#L9-L116) |

## 1. LangGraph code

### Two graph shapes

The four agents do **not** share one topology. Two are genuinely single-step and
use a single-node wrapper; two make a branching decision and are real multi-node
graphs.

| Agent | Shape | Why |
| --- | --- | --- |
| `workflow-planning` | Single node | Classification is one structured decision. There is nothing to branch on before the answer exists. |
| `transaction-explanation` | Single node | Explaining a transaction is one informational step. |
| `suspicious-activity` | Multi-node, 1 conditional edge | Whether the customer asked us to *change* the account changes what the agent may do. |
| `dispute-planning` | Multi-node, 1 conditional edge | A claim missing required facts cannot be assessed for evidence. |

**A single node is the correct shape for a genuinely single-step agent.** Adding
nodes to `workflow-planning` or `transaction-explanation` would add latency and
cost without adding a decision. The multi-node graphs exist where there is a real
branch, not to demonstrate LangGraph for its own sake.

### Single-node agents

[`build_agent_graph`](../src/agents/python/app/agents/base.py#L16-L25) compiles
this graph for the two single-step agents:

```text
START -> analyze -> END
```

using the two-field [`AgentState`](../src/agents/python/app/agents/base.py#L11-L14):

```python
class AgentState(TypedDict):
    request: AgentRequest
    result: AgentResult | None
```

The `analyze` node calls `reason(agent, instructions, state["request"])` and
returns `{"result": result}`.

| Agent | Code | Responsibility encoded in its prompt |
| --- | --- | --- |
| `workflow-planning` | [`planning.py`](../src/agents/python/app/agents/planning.py) | Classify intent, recommend one specialist, assess risk, and recommend whether approval is needed; never act. |
| `transaction-explanation` | [`transaction_explanation.py`](../src/agents/python/app/agents/transaction_explanation.py) | Explain transaction status using supplied context without inventing account or merchant data. |

### `dispute-planning`: conditional on claim completeness

[`dispute.py`](../src/agents/python/app/agents/dispute.py):

```text
START -> extract_claim -> validate_completeness -> (conditional)
                                                   |-> request_more_info -> END
                                                   |-> assess_evidence -> draft_plan -> END
```

State carries real intermediate values between nodes, not just the terminal
result:

```python
class DisputeState(TypedDict, total=False):
    request: AgentRequest
    claim: DisputeClaim              # written by extract_claim
    completeness: CompletenessCheck  # written by validate_completeness, selects the branch
    assessment: EvidenceAssessment   # written by assess_evidence, read by draft_plan
    used_fallback: bool
    result: AgentResult
```

Each node has its own output schema rather than producing a whole `AgentResult`.

Two safety properties are enforced by code rather than by prompt:

- `validate_completeness` recomputes `is_complete` from the extracted claim's
  missing fields, so a model cannot declare an empty claim complete and skip the
  information request.
- **Both** terminal branches set `requires_approval=True`. Preparing a dispute
  plan is not filing one, and the human gate does not depend on how complete the
  claim happened to be.

### `suspicious-activity`: conditional on action versus explanation

[`suspicious_activity.py`](../src/agents/python/app/agents/suspicious_activity.py):

```text
START -> gather_signals -> classify -> (conditional)
                                       |-> plan_protective_action -> END
                                       |-> explain_activity -> END
```

```python
class SuspiciousState(TypedDict, total=False):
    request: AgentRequest
    signals: SignalSet                       # observed facts vs hypotheses; selects the branch
    classification: ActivityClassification   # category and severity
    used_fallback: bool
    result: AgentResult
```

The branch keys on `signals.action_requested`, **not** on severity. Describing
risk is informational and completes immediately; freezing, blocking, or closing
an account is an action and is gated behind approval. Routing on severity
instead would send a high-risk *informational* request to the approval queue.

`gather_signals` OR-s the model's `action_requested` with a deterministic
keyword check, so a model that overlooks "freeze my card" cannot route the
request away from the approval gate. `requires_approval` is set by the branch,
never by model output.

### Fallback and execution mode

Every node calls
[`structured_step`](../src/agents/python/app/model.py), which returns `None` when
no model endpoint is configured and `ALLOW_FALLBACK` permits degradation, so each
node applies its own deterministic path. Any node falling back sets
`used_fallback`, and the terminal node reports `execution_mode="fallback"` for
the whole invocation. With `ALLOW_FALLBACK=false` — the deployed configuration —
a missing model endpoint raises `ModelUnavailableError` instead.

### Invocation cost

A multi-node graph issues **one model call per node it visits**: up to four for
`dispute-planning` and three for `suspicious-activity`. `AGENT_INVOKE_TIMEOUT_SECONDS`
is therefore deployed at 90s, and the orchestrator's
`FOUNDRY_ATTEMPT_TIMEOUT_SECONDS` at 100s so the agent's own timeout surfaces
first.

Workflow sequencing, routing, approval, durability, and recovery remain C#
application logic, not LangGraph nodes.

[`registry.py`](../src/agents/python/app/agents/registry.py#L9-L18) imports those four
already-compiled graphs and maps the `AgentName` enum to the correct graph. There is
no dynamic graph discovery.

### Typed invocation contract

[`contracts.py`](../src/agents/python/app/contracts.py#L9-L40) defines the Pydantic
boundary:

- `AgentName` limits identity to the four registered names.
- `contract_version` identifies the `"1.0"` request/result boundary.
- `AgentRequest.message` is required and nonempty.
- `trace_id`, `workflow_id`, `input`, `metadata`, and `context` carry invocation
  metadata.
- Top-level `context` is authoritative; legacy `input.context` is promoted when the
  top-level value is absent.
- `AgentResult` requires agent identity, status, trace ID, intent, summary, risk,
  approval recommendation, recommended action, and next step.
- `execution_mode` reports whether the result came from the live model or the
  deterministic fallback.
- The planner can set `selected_agent`.
- `evidence` is a list of reasoning strings, not uploaded evidence files.

`AgentRequest` allows extra fields so the Foundry envelope can evolve without
immediately failing Pydantic validation. The C# side is stricter about the subset it
accepts from `AgentResult`; see
[`TryReadAgentResult`](../src/application/WorkflowService.cs#L767-L824).

### Model call

[`model._model`](../src/agents/python/app/model.py#L15-L45) chooses one of three
paths:

1. If `BANKING_AGENT_PROJECT_ENDPOINT` exists, create `ChatOpenAI` against the project's
   `/openai/v1/` endpoint and acquire an Entra token for
   `https://ai.azure.com/.default`. The deployer job populates this value with the
   non-reserved `BANKING_AGENT_PROJECT_ENDPOINT` name; the runtime also accepts the
   legacy `FOUNDRY_PROJECT_ENDPOINT` name for compatibility.
2. Otherwise, if `AZURE_OPENAI_ENDPOINT` exists, create `AzureChatOpenAI` and acquire
   an Entra token for `https://cognitiveservices.azure.com/.default`.
3. Otherwise, report that no model is configured.

[`reason`](../src/agents/python/app/model.py#L47-L70) wraps the model with
`with_structured_output(AgentResult)`, sends the agent-specific instructions as the
system message, and sends trace ID, customer request, and the normalized specialist
context as the user message. It then overwrites `agent`, `status`, `trace_id`,
`contract_version`, and `execution_mode` so those operational fields come from the
runtime rather than the model.

If no model is configured and `ALLOW_FALLBACK` is explicitly set to an affirmative
value for local development,
[`_local_result`](../src/agents/python/app/model.py#L73-L143) returns deterministic
rule-based results for every agent. This makes local tests and demonstrations
repeatable and marks every result with `execution_mode: fallback`. Hosted production
registrations set `ALLOW_FALLBACK=false`, so missing model configuration fails the
invocation rather than returning a success-shaped fallback.

### Versioned planner-to-specialist context handoff

The C# service builds planner handoff fields under `context` in
[`WorkflowService`](../src/application/WorkflowService.cs):

```text
parameters["context"]["planner_summary"]
parameters["context"]["planner_evidence"]
parameters["context"]["planner_selected_agent"]
parameters["context"]["selected_agent"]
```

For typed-envelope tools, [`FoundryMcpClient`](../src/infrastructure/FoundryMcpClient.cs)
emits the versioned `1.0` envelope with `context` both at the typed top level and
within the retained `input` dictionary. It no longer adds fake MCP metadata to that
non-MCP body. [`AgentRequest`](../src/agents/python/app/contracts.py) treats the
top-level value as authoritative and promotes legacy `input.context` when required.
[`reason`](../src/agents/python/app/model.py) sends that normalized value to the
selected specialist model. A shared JSON fixture exercises the exact C# serialization
and Python Pydantic boundary.

This handoff is orchestrator-mediated context passing, not direct agent-to-agent
communication. The real MCP contract is implemented only for the transaction
explanation vertical slice; the remaining three agent migrations are still future
work.

## 2. Foundry hosted runtime

### Process startup

[`Dockerfile.hosted`](../src/agents/python/Dockerfile.hosted) installs the Python
requirements, copies `app/`, switches to non-root user `1000`, and runs:

```text
python -m app.hosted
```

At module import,
[`hosted.py`](../src/agents/python/app/hosted.py#L20-L22):

1. reads `BANKING_AGENT_KIND`;
2. converts it to the `AgentName` enum;
3. selects one compiled graph from the registry; and
4. constructs `InvocationAgentServerHost`.

This is how one image becomes four separately addressable agents: Foundry starts
separate hosted-agent registrations from the same image with a different
`BANKING_AGENT_KIND`.

### Request handling

[`handle_invoke`](../src/agents/python/app/hosted.py) handles the typed envelope:

1. reads the Foundry HTTP request body;
2. validates it as `AgentRequest`;
3. invokes the selected graph with
   `{"request": payload, "result": None}`;
4. enforces `AGENT_INVOKE_TIMEOUT_SECONDS` (30 seconds by default);
5. serializes the resulting `AgentResult`; and
6. returns JSON.

Boundary behavior is explicit:

| Condition | Response |
| --- | --- |
| Invalid JSON or `AgentRequest` | HTTP 400, `invalid_request` |
| Graph exceeds timeout | HTTP 504, `timeout` |
| Graph/model raises | HTTP 500, safe `agent_error` |
| Valid result | HTTP 200 with the `AgentResult` JSON body |

For `BANKING_AGENT_KIND=transaction-explanation`, the same hosted image also exposes
a JSON-RPC MCP handler at `/mcp` and accepts JSON-RPC bodies posted through
`/invocations`. The MCP handler returns `initialize` capabilities, exposes exactly
the `transaction.explain` tool from `tools/list`, and invokes the transaction graph
from `tools/call`. Other hosted-agent kinds return an empty MCP tool list.

The real ASGI boundary is exercised in
[`test_hosted.py`](../src/agents/python/tests/test_hosted.py); graph routing and
fallback behavior are exercised in
[`test_agents.py`](../src/agents/python/tests/test_agents.py).

## 3. How the C# orchestrator coordinates agents

### Planner call

[`ExecuteRoutingAsync`](../src/application/WorkflowService.cs#L189-L355) begins with
the persisted workflow and creates planner parameters containing:

- customer message;
- durable workflow ID;
- durable trace ID;
- `planning` status; and
- the current correlation ID when available.

It calls `InvokeAgentAsync("workflow.plan", "workflow-planning", ...)`. The tool name
is resolved to the planner's Foundry endpoint through the Terraform-generated map.
The response becomes a durable `workflow.plan` event only after
`TryReadAgentResult` verifies the response.

### Routing authority

The planner's valid `selected_agent` is the authoritative specialist route. The C#
[`WorkflowRoutingPolicy`](../src/application/WorkflowRoutingPolicy.cs) still runs as
a guardrail, but it can only escalate approval by changing
`requires_approval = false` to `true`; it never replaces a valid planner-selected
specialist and never de-escalates planner approval.

If the planner omits `selected_agent` or returns an unrecognized specialist name,
the orchestrator falls back to `WorkflowRoutingPolicy` and persists a
`workflow.route_fallback` event with the planner value, policy route, winning route,
and reason code. If the planner and policy disagree on agent selection or approval
for a valid planner route, the orchestrator persists a
`workflow.route_disagreement` event with planner agent/approval, policy
agent/approval, and the winning agent/approval. These events are part of the
workflow event stream returned by the API and shown by the Web UI.

### Specialist call

The selected C# route maps to one tool:

| C# route | Tool | Foundry registration |
| --- | --- | --- |
| `transaction-explanation` | `transaction.explain` | `transaction-explanation` |
| `suspicious-activity` | `suspicious.assess` | `suspicious-activity` |
| `dispute-planning` | `dispute.plan` | `dispute-planning` |

The tool map is defined twice for separate purposes:

- `WorkflowService.SpecialistTools` selects the tool name.
- [`foundry_tool_endpoints`](../apps/main.tf#L27-L32) maps that tool name to a
  concrete Foundry hosted-agent URL.

After validating the specialist result, the C# service—not the Python agent—sets
`Completed` or `WaitingForApproval` and persists the terminal event in PostgreSQL.
Python's `requires_approval` is not authoritative.

### Transport and authentication

[`FoundryMcpClient.InvokeAsync`](../src/infrastructure/FoundryMcpClient.cs):

1. resolves the tool name to its configured endpoint;
2. uses MCP JSON-RPC when the tool appears in `FOUNDRY_MCP_TOOL_ENDPOINTS`;
3. otherwise builds the typed Foundry invocation JSON envelope;
4. obtains a managed-identity token for `https://ai.azure.com/.default`;
5. sends `POST` with bearer authentication;
6. forwards `x-correlation-id` for typed-envelope calls when present;
7. retries configured transient failures; and
8. returns the HTTP body in `McpToolResult.Data["response_body"]`.

The orchestrator registers this implementation through
[`AddHttpClient<IMcpClient, FoundryMcpClient>`](../src/orchestrator/Program.cs#L137-L153).
`IMcpClient` is now accurate for MCP-enabled tools. It remains the adapter interface
for legacy typed-envelope tools until those agents migrate.

### Result validation

[`TryReadAgentResult`](../src/application/WorkflowService.cs#L767-L824) rejects a
response unless:

- transport status is `ok`;
- `response_body` exists;
- JSON deserializes;
- result status is `ok`;
- intent and summary are nonempty; and
- returned agent identity exactly matches the expected registration.

This stops a response from one hosted agent being accepted as another. The accepted
specialist intent and summary become durable workflow data; the C# route controls
approval.

## 4. How the agents are built and deployed to Foundry

### Step 1: Build the shared image

[`task app:build-hosted-agents`](../tasks/Taskfile.app.yml#L68-L81) runs an ACR build
for `Dockerfile.hosted` and publishes:

```text
hosted-agents:<eight-character-commit>
hosted-agents:latest
```

Terraform uses the immutable `image_tag` in
[`hosted_agents_image`](../apps/main.tf#L16). The same immutable image is supplied to
all four registrations.

### Step 2: Terraform configures the deployer job

[`apps/main.tf`](../apps/main.tf#L34-L55) declares four `{name, kind}` objects.
[`apps/agent-deployer.tf`](../apps/agent-deployer.tf) creates a manually triggered
Azure Container Apps Job and injects:

| Environment variable | Source and purpose |
| --- | --- |
| `AZURE_CLIENT_ID` | Agent-deployer managed identity |
| `FOUNDRY_PROJECT_ENDPOINT` | Foundry project data-plane endpoint |
| `HOSTED_AGENT_IMAGE` | Immutable ACR image |
| `AZURE_AI_MODEL_DEPLOYMENT_NAME` | Model deployment name |
| `AGENT_DEFINITIONS` | JSON array of the four registrations |

Terraform also guarantees the job is created after the required ACR and Foundry role
assignments.

### Step 3: The job registers versions in Foundry

[`deploy.py`](../src/agents/deployer/deploy.py#L28-L289) uses
`ManagedIdentityCredential` and the `https://ai.azure.com/.default` scope. For each
definition it:

1. queries the existing agent and versions;
2. skips an active version when image, kind, and model already match;
3. otherwise creates the agent or posts a new version;
4. registers `kind: hosted`, 0.5 CPU, 1 GiB memory, and invocation protocol `2.0.0`;
5. sets `BANKING_AGENT_KIND` and `AZURE_AI_MODEL_DEPLOYMENT_NAME`; and
6. waits until the version is `active` or `running`.

The registration does not create four images. It creates four Foundry agent identities
and versions that all point to the same image but start it with different graph
selection.

### Step 4: Foundry pulls and starts the image

[`apps/roles.tf`](../apps/roles.tf#L40-L54) grants:

- the Foundry project identity `AcrPull` on the registry; and
- the Foundry project identity `Foundry User` on the Foundry account.

Foundry can then pull the shared image, start the `app.hosted` process, and route
invocation-protocol requests to it.

### Step 5: Grant each running agent model access

The hosted-agent instance identity does not exist until Foundry creates a version.
Therefore Terraform cannot assign its model role in advance.

[`scripts/deploy-hosted-agents.sh`](../scripts/deploy-hosted-agents.sh#L9-L116)
starts the Container Apps Job, waits for success, lists each active Foundry version,
extracts its `instance_identity.principal_id`, and idempotently grants
`Cognitive Services OpenAI User` on the Foundry account.

This post-deploy role assignment is an intentional lifecycle exception that is driven
by a repository script because the principal IDs are generated by Foundry at runtime.
The desired roles and deployment mechanism remain represented by Terraform and the
versioned script.

### Step 6: The orchestrator calls the registered endpoints

Terraform builds these URLs in
[`foundry_tool_endpoints`](../apps/main.tf#L27-L32):

```text
.../agents/workflow-planning/endpoint/protocols/invocations?api-version=v1
.../agents/transaction-explanation/endpoint/protocols/invocations?api-version=v1
.../agents/suspicious-activity/endpoint/protocols/invocations?api-version=v1
.../agents/dispute-planning/endpoint/protocols/invocations?api-version=v1
```

[`apps/orchestrator.tf`](../apps/orchestrator.tf#L115-L139) injects that JSON map and
retry configuration into the orchestrator. The orchestrator identity receives
`Foundry Agent Consumer` in
[`apps/roles.tf`](../apps/roles.tf#L19-L25).

## 5. Runtime model enforcement

[`deploy.py`](../src/agents/deployer/deploy.py) registers every hosted agent with
`BANKING_AGENT_KIND`, `AZURE_AI_MODEL_DEPLOYMENT_NAME`,
`FOUNDRY_PROJECT_ENDPOINT`, and `ALLOW_FALLBACK=false`. All four values participate
in version matching, so changing runtime behavior creates a new hosted-agent version.

Every result carries `contract_version` and `execution_mode`. The orchestrator records
those non-PII fields in workflow event details and OpenTelemetry tags. Production smoke
checks require both planner and specialist events to report `execution_mode: model`;
missing evidence or any fallback result fails the smoke run.

## 6. What is durable and what is not

| State | Owner | Durable? |
| --- | --- | --- |
| Workflow status, version, route, intent | C# orchestrator + PostgreSQL | Yes |
| Workflow events and audit trail | C# orchestrator + PostgreSQL | Yes |
| Evidence files and metadata | C# orchestrator + PostgreSQL | Yes |
| Approval, action execution, support case | C# orchestrator + PostgreSQL | Yes |
| Python `AgentState` during one invocation | Foundry-hosted process | No |
| Planner model context | Planner request only | No |
| Specialist model context | Specialist request only | No |
| Cross-agent memory | Not implemented | No |
| LangGraph checkpoint/thread | Not configured | No |

For the proposed Agent Framework, MCP, shared-artifact, outbox, and Agent Host
Protocol evolution, see
[Recommended shared-state evolution](mvp-implementation-operations-guide.md#recommended-shared-state-evolution).
