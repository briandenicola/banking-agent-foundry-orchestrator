# Issue close action plan

This document is the working plan for closing the remaining backlog issues in a way that is explicit, testable, and observable. It is meant to replace vague status updates with a concrete execution plan.

## Working assumptions

- These issues are not considered complete until the acceptance criteria are met and the relevant validation steps pass.
- The plan below is ordered to reduce rework: #20 first, then #18, then #17.
- Each issue must end with evidence in source, tests, and deployment/smoke output before it is marked closed.

## Issue #20 — Fix hosted-agent planner context and live model configuration

### Why this issue remains open

The current implementation still has a contract/configuration gap between planner output and specialist execution, and the hosted-agent path can still fall back silently when the model configuration is incomplete.

### Close tasks

1. Fix the planner-to-specialist contract
   - Ensure the C# orchestrator sends planner output through one versioned contract that the Python runtime consumes consistently.
   - Remove any logic that depends on the planner context being present only in a legacy or duplicated location.
   - Keep the contract backward-compatible where possible, but make the canonical path explicit and tested.

2. Align the C# envelope with the Python boundary
   - Confirm the request body sent to the hosted agent preserves the planner summary, evidence, and selected specialist in the exact location the Python `AgentRequest` contract reads.
   - Add a regression test that serializes the C# payload and validates the Python-facing boundary.

3. Make live-model configuration explicit
   - Ensure the hosted-agent registration sets every runtime variable required for a real Foundry model invocation.
   - Document which values are expected from the platform and which are required from deployment configuration.
   - Fail loudly when a live model path is expected but the runtime is not configured correctly.

4. Make fallback observable
   - Ensure the execution path exposes whether the result came from the live model or the deterministic fallback.
   - Ensure the smoke output and deployment evidence distinguish live execution from fallback.

5. Add validation and evidence
   - Add/extend unit and integration tests for the planner-context handoff and model-configuration behavior.
   - Run smoke validation after deployment and save the output used to confirm the fix.

### Definition of done

- Planner output reaches the selected specialist through the canonical, versioned contract.
- The hosted-agent runtime no longer silently uses the fallback path without an observable signal.
- Tests cover the contract and the configuration path.
- Smoke evidence shows the workflow reaches the expected live-model path.

---

## Issue #18 — Implement real MCP discovery and invocation for Foundry specialist agents

### Why this issue remains open

The current boundary is still an adapter-shaped wrapper around Foundry hosted-agent HTTP calls. The issue requires a standards-based MCP discovery/invocation path that the orchestrator can discover and invoke through a typed tool contract.

### Close tasks

1. Define the MCP tool contract
   - Define the tool schemas for planning, transaction explanation, suspicious activity assessment, and dispute planning.
   - Make the schemas explicit in code and in the contract tests.

2. Implement the MCP transport boundary
   - Introduce a real MCP-style discovery path (`tools/list`) and invocation path (`tools/call`) rather than relying only on the current adapter envelope.
   - Ensure requests carry workflow ID, trace ID, correlation metadata, and typed arguments.

3. Add authentication and error handling
   - Use the appropriate managed identity / Entra flow for the selected hosting pattern.
   - Map transient and permanent failures to workflow-safe errors and durable workflow events.
   - Validate timeouts, retries, malformed responses, and cancellation behavior.

4. Integrate with the orchestrator
   - Make the orchestrator discover the tools and invoke them through the typed MCP boundary.
   - Ensure tool discovery failures surface as a readiness problem rather than silently continuing.

5. Add tests and deployment evidence
   - Add tests for tool discovery, tool invocation, schema validation, and failure mapping.
   - Deploy and verify the MCP-enabled path through smoke and runtime logs.

### Definition of done

- The production path uses MCP protocol messages for tool discovery and invocation.
- Missing or incompatible tools cause the orchestrator to fail readiness rather than continue with an invalid contract.
- Durable workflow errors and telemetry are produced for transient and permanent MCP failures.
- Deployment and smoke evidence confirm the MCP-enabled path is active.

---

## Issue #17 — Adopt Microsoft Agent Framework for C# workflow orchestration

### Why this issue remains open

The repo now has orchestration-related code, but the production path still depends on procedural orchestration rather than Agent Framework as the authoritative workflow engine.

### Close tasks

1. Replace the procedural orchestration path
   - Move the primary workflow execution path in `WorkflowService` to Agent Framework workflow steps.
   - Represent planner execution, routing, specialist invocation, approval pause/resume, recovery, and completion as explicit workflow steps.

2. Preserve workflow guarantees
   - Keep the existing API contract (`POST /api/v1/workflows` returning `202 Accepted` after durable draft persistence).
   - Preserve PostgreSQL as the authoritative state store for workflow state, approvals, evidence, and recovery claims.
   - Preserve explicit approval boundaries before sensitive actions.

3. Add workflow resilience behavior
   - Implement cancellation, retry, timeout, and failure transitions in the Agent Framework workflow.
   - Ensure duplicate execution and recovery behavior are safe.

4. Add telemetry and observability
   - Ensure OpenTelemetry spans and workflow trace data identify workflow and agent steps without logging PII.
   - Ensure status transitions remain visible through the existing API and Web UI contracts.

5. Add tests and documentation
   - Add tests for resume, duplicate execution safety, timeout, and approval-driven pause/resume.
   - Update the implementation and operations documentation to reflect the actual execution path.

### Definition of done

- The production execution path uses Microsoft Agent Framework rather than only package references or test doubles.
- Approval and recovery remain durable and compatible with the existing API and persistence model.
- Tests demonstrate workflow pause/resume and duplicate-execution safety.
- Documentation reflects the real implementation.

---

## Recommended execution order

1. Close #20 first because it fixes the contract and model-config gap that the later issues depend on.
2. Close #18 next because it builds the tool boundary the orchestrator needs.
3. Close #17 last because it replaces the orchestration engine on top of the fixed contract and tool boundary.

## Evidence required before any issue is marked closed

- Code changes are committed and pushed.
- Relevant unit/integration tests pass.
- Deployment or local smoke validation confirms the behavior.
- Documentation reflects the final implementation.
- The issue body or a linked tracking document clearly shows the acceptance criteria are satisfied.
