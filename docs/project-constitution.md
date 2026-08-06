# Project Constitution

**Version 1.1** — see [Amendment history](#amendment-history).

## Purpose
Create a banking-focused agentic reference application in which a C# orchestrator agent built with Microsoft Agent Framework coordinates a workflow of LangGraph-hosted agents in Microsoft Foundry through MCP tools.

## Core objectives
- Build a C# orchestrator agent using Microsoft Agent Framework as the primary control plane.
- Use MCP to load Microsoft Foundry-hosted LangGraph agents as tools for specialized reasoning and action workflows.
- Deploy containerized services to Azure Container Apps with Entra ID-based authentication.
- Use Azure HorizonDB as the operational data store for workflow state and audit history.
- Provision infrastructure with Terraform and automate builds and deployments with GitHub Actions.
- Never use keys for authentication; always use Microsoft Entra ID.
- Make sensitive actions require explicit approval and produce detailed traces.

## Architectural principles
1. Security and identity: No API keys or shared secrets for service authentication. Entra ID, managed identity, and workload identity are mandatory.
2. Layered architecture: Keep domain logic independent from frameworks and infrastructure. Use a Domain → Application → Infrastructure → API structure.
3. Agentic design: Build multi-step workflows with explicit approvals, tool boundaries, and traceable decisions. The C# orchestrator agent uses Microsoft Agent Framework and loads Foundry-hosted LangGraph agents as MCP tools rather than embedding their logic directly.
4. Observability and traceability: Every workflow emits structured logs, OpenTelemetry traces, and correlation IDs. Never log secrets or PII.
5. Cloud-native delivery: Azure Container Apps, Terraform, and GitHub Actions are the default deployment path. Containers must be non-root and images must be built reproducibly.
6. API-first and versioned: Public APIs are versioned, validated at the boundary, and return ProblemDetails for failures.
7. Data discipline: Persist workflow state and audit metadata using parameterized access patterns, pagination on lists, and audit fields where applicable.

## Architectural guardrails
- C# is the primary application language for orchestration, policy enforcement, and API integration.
- The C# orchestrator agent uses Microsoft Agent Framework and loads Microsoft Foundry-hosted LangGraph agents as MCP tools.
- Communication between the orchestrator and remote agents should use MCP or versioned HTTP contracts where a non-MCP gateway is required.
- Model access is made directly against Microsoft Foundry by the hosted agents. There is no AI gateway in the current architecture; see [ADR 0001](decisions/0001-remove-litellm-gateway.md) for why LiteLLM was removed and the conditions under which a gateway would be reintroduced.
- The orchestrator must enforce approval gates before any sensitive banking action.
- Every workflow step must emit traceable, structured audit data.
- The system must be deployable as Azure Container Apps with managed identity and secretless access where possible.

## Non-goals for v1
- Full production banking operations or live transaction execution against real bank systems.
- Advanced multi-tenant enterprise security controls beyond the initial approval and audit patterns.
- A fully autonomous agent that can act without guardrails.
- A deviation from Entra ID-only authentication without an approved architecture decision record.

## Document hierarchy and governance
- This constitution is the highest authority for this repository. Lower-level documents, plans, and implementation tasks must align with it.
- The expected decision order is: constitution → active spec → implementation plan → tasks → backlog → local judgment.
- Any change to this constitution requires a documented amendment and version bump.
- Architecture decision records live in `docs/decisions/` and are numbered sequentially.
- Before declaring work complete, run the local quality gate: build, tests, formatting, and targeted validation for changed services.
- Any deviation from Entra ID-only authentication, no-key policy, or agent framework guidance requires an architecture decision record before merge.

## Definition of done

A green build is necessary but never sufficient. "It compiles and the suite passes" proves no specific acceptance criterion was met — it only proves nothing else broke. Work has repeatedly been reported as complete in this repository while acceptance criteria went unmet; this section exists to stop that.

An issue may be closed only when every box below is checked, with the evidence pasted into the closing comment:

- [ ] Every acceptance criterion is quoted verbatim, each paired with the specific file and line, or the specific test name, that satisfies it.
- [ ] At least one test exists that **fails if the change is reverted**, and that failure has actually been observed — not assumed.
- [ ] Behaviour that only manifests at runtime (auth, networking, telemetry, migrations) has deployed smoke or log evidence. A passing unit test does not establish runtime behaviour.
- [ ] Documentation has been re-read against the code, not against the plan or the intent.
- [ ] Any criterion **not** met is split into a follow-up issue and linked before closing.

Prefer partial closure over optimistic closure: close what genuinely shipped, and open a linked follow-up carrying the remainder. Reporting a gap honestly costs one comment; discovering it later costs a full re-audit and the credibility of every other status in the backlog.

### Claims in documentation

- Present tense describes what is true of `main` today.
- Anything aspirational must be explicitly labelled **Target** or **Planned**. Never describe an intended design in the present tense.
- When a capability is partially implemented, say what is implemented and what is not, rather than choosing the more flattering summary.

## Success criteria
- A user can submit a banking request and receive a guided multi-step workflow response.
- The system can distinguish between informational and sensitive actions.
- The workflow pauses for approval before executing any sensitive action.
- Infrastructure can be provisioned by Terraform from a clean environment.
- Logs and traces identify the request, agent decision, approval event, and final action state.

## Amendment history

| Version | Date | Change | Record |
|---|---|---|---|
| 1.1 | 2026-08-06 | Removed the requirement to use LiteLLM as the AI gateway. Hosted agents call Microsoft Foundry directly; there is no gateway in the current architecture. | [ADR 0001](decisions/0001-remove-litellm-gateway.md) |
| 1.0 | 2026-07-29 | Initial constitution. | — |
