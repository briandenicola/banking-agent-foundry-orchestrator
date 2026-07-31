# Demo scenarios

The Web UI provides six guided scenarios backed by synthetic, non-PII transactions.
Select a scenario card to populate the request and submit it. The server resolves the
scenario by ID, so failure and timeout behavior does not depend on prompt wording.

Demo scenarios are enabled in the deployed orchestrator with
`DEMO_SCENARIOS_ENABLED=true`. When disabled, scenario IDs are rejected while ordinary
workflow requests continue to work.

## Synthetic transactions

| Reference | Account | Merchant | Amount | Status | Purpose |
| --- | --- | --- | ---: | --- | --- |
| `DEMO-TXN-1001` | `DEMO-CHECKING-001` | Northwind Market | USD 84.27 | Settled | Dispute paths |
| `DEMO-TXN-1002` | `DEMO-CHECKING-001` | Metro Transit | USD 12.40 | Pending | Explanation and fault paths |
| `DEMO-TXN-1003` | `DEMO-CHECKING-001` | Alpine Digital | USD 249.99 | Settled, suspicious | Suspicious-activity path |

These fixed records contain no names, addresses, email addresses, real account
numbers, or other customer data. The database migration uses stable IDs and EF seed
operations, making repeated migration runs idempotent.

## Expected outcomes

| Scenario | Expected UI state | Durable workflow and audit evidence | Telemetry outcome |
| --- | --- | --- | --- |
| Explain a pending transaction | `Completed`; no approval card | Transaction specialist selected; `mcp.invoked` and `workflow.completed` events | Successful lifecycle and Hosted Agent spans |
| Review suspicious activity | `Completed`; review summary shown | Suspicious-activity specialist selected; no action execution | Successful lifecycle and Hosted Agent spans |
| Approve a dispute | Initially `WaitingForApproval`; after approval `Completed` with support-case card | Approval event, one completed `dispute.support_case.create` action, and one support case | Approval and persistence spans succeed with `support_case.created=true` |
| Reject a dispute | Initially `WaitingForApproval`; after rejection `Rejected` with no support case | Rejection decision and approval event; no action execution or support case | Approval span succeeds with `Rejected` outcome |
| Simulate an agent failure | `Failed` with the durable timeline available | Specialist invocation event records the synthetic error and `workflow.failed` closes the audit trail | Hosted Agent and lifecycle spans have error outcomes |
| Simulate an agent timeout | `Failed` with the durable timeline available | `workflow.failed` records the safe specialist-failure result | Hosted Agent span records `TimeoutException`; lifecycle outcome is failed |

The synthetic fault scenarios execute after the real planning step and deterministic
routing. They do not call the specialist Hosted Agent, sleep, or rely on network
instability.

## Reset and rerun

Use **Reset workspace** to clear the current browser view, then select any scenario
again. Every run creates a new workflow and trace ID, so no database cleanup is
required. Approved action idempotency remains scoped to that workflow; refreshing or
resubmitting the same approval cannot create a duplicate support case.

For trace verification and Application Insights queries, follow
[`observability.md`](observability.md).
