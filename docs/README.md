# Documentation

This index organizes the banking-agent documentation by audience and task.

> **Current implementation:** Start with the
> [MVP implementation and operations guide](mvp-implementation-operations-guide.md).
> Product, constitution, and planning documents describe the target architecture and
> may include work that is tracked but not yet implemented.

## Start here

| Need | Document |
| --- | --- |
| Understand, deploy, operate, or troubleshoot the current MVP | [MVP implementation and operations guide](mvp-implementation-operations-guide.md) |
| Understand product behavior and acceptance expectations | [Functional specification](functional-spec.md) |
| Understand architecture and implementation boundaries | [Technical specification](technical-spec.md) |
| Trace the LangGraph agents and Foundry runtime through the code | [Agent implementation and Foundry runtime](agent-implementation.md) |
| Run local and CI test suites | [Testing guide](testing.md) |
| Run a repeatable demonstration | [Demo scenarios](demo-scenarios.md) |

## Product and governance

| Document | Purpose |
| --- | --- |
| [Project constitution](project-constitution.md) | Defines architectural principles, security guardrails, and delivery expectations. |
| [Functional specification](functional-spec.md) | Defines the target product journey, workflow behavior, and approval requirements. |
| [Technical specification](technical-spec.md) | Describes the target architecture and calls out important current implementation gaps. |

## Implementation and operations

| Document | Purpose |
| --- | --- |
| [MVP implementation and operations guide](mvp-implementation-operations-guide.md) | Code-referenced source of truth for workflow lifecycle, agents, PostgreSQL, Foundry, authentication, deployment, rollback, troubleshooting, and smoke verification. |
| [Agent implementation and Foundry runtime](agent-implementation.md) | Traces LangGraph state, graph construction, typed contracts, model calls, C# handoff, shared image packaging, Foundry registration, identities, and runtime behavior to exact source locations. |
| [Testing guide](testing.md) | Explains test categories, prerequisites, local commands, and CI coverage. |
| [Workflow observability](observability.md) | Defines telemetry and correlation behavior with ready-to-run Application Insights queries. |
| [Terraform remote state](remote-state.md) | Covers Azure Blob state, OIDC authentication, environment separation, bootstrap, and migration. |
| [Demo scenarios](demo-scenarios.md) | Documents synthetic non-PII scenarios and their expected workflow outcomes. |

## Planning and backlog

| Document | Purpose |
| --- | --- |
| [Phase plan](phase-plan.md) | Organizes the target delivery into architecture and implementation phases. |
| [First sprint plan](first-sprint-plan.md) | Records the scope and exit criteria for the initial end-to-end slice. |
| [Implementation backlog](implementation-backlog.md) | Lists implementation work and acceptance criteria; active execution is tracked in GitHub Issues. |

## Browse by task

- **Trace a request through the code:** [Workflow lifecycle](mvp-implementation-operations-guide.md#2-workflow-lifecycle)
- **Understand agent communication and shared state:** [Agent implementation and Foundry runtime](agent-implementation.md)
- **Understand PostgreSQL durability:** [PostgreSQL state, audit, and recovery](mvp-implementation-operations-guide.md#4-postgresql-state-audit-and-recovery)
- **Configure authentication:** [Authentication and tenant prerequisites](mvp-implementation-operations-guide.md#7-authentication-and-tenant-prerequisites)
- **Deploy to Azure:** [Deployment](mvp-implementation-operations-guide.md#10-deployment)
- **Operate or troubleshoot:** [Operating the MVP](mvp-implementation-operations-guide.md#11-operating-the-mvp) and [Troubleshooting](mvp-implementation-operations-guide.md#12-troubleshooting)
- **Roll back:** [Rollback](mvp-implementation-operations-guide.md#13-rollback)
- **Verify a deployment:** [Smoke and acceptance evidence](mvp-implementation-operations-guide.md#14-smoke-and-acceptance-evidence)
- **View the UI workflow:** [UI workflow walkthrough](mvp-implementation-operations-guide.md#ui-workflow-walkthrough)
