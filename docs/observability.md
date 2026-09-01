# Workflow observability

The Web UI and orchestrator export OpenTelemetry traces to the configured Application
Insights resource. Incoming HTTP requests, outbound HTTP dependencies, and the custom
`BankingAgent.Workflow` activity source share W3C trace context.

## Correlation model

Each browser request receives an `x-correlation-id` response header. A caller-supplied
value is preserved when it is 128 characters or fewer; otherwise the service generates
one. The Web UI forwards this header to the orchestrator, and the orchestrator forwards
it to Hosted Agent calls. Standard `traceparent` propagation links each HTTP hop.

Workflow activities use these safe attributes:

| Attribute | Purpose |
| --- | --- |
| `workflow.id` | Durable workflow identifier used across separate submit, lookup, and approval requests |
| `workflow.trace_id` | Trace identifier returned in the workflow API contract and Hosted Agent payload |
| `correlation.id` | Operator- or service-provided request correlation value |
| `agent.name`, `tool.name`, `agent.status` | Hosted Agent boundary and outcome |
| `agent.execution_mode`, `agent.contract_version` | Whether the result came from the live model or the deterministic fallback, and the contract version in use |
| `agent.discovered_tools`, `agent.discovered_tools.count` | MCP tool names returned by `tools/list` at discovery |
| `approval.decision` | `approve` or `reject` only |
| `action.type`, `support_case.created` | Approved action outcome without case contents |
| `workflow.status`, `workflow.version`, `workflow.expected_version` | State-machine position and optimistic-concurrency values |
| `workflow.recovery.attempt_count`, `workflow.recovery.max_attempts`, `workflow.recovery.next_attempt_at` | Recovery attempt limiting and backoff |
| `workflow.operation`, `persistence.found`, `duration_ms` | Operation name, lookup result, and elapsed time |
| `outcome`, `error.type` | Safe result metadata; exception messages are not recorded |

The application does not attach user messages, approval reasons, evidence content,
tokens, account data, or raw downstream response bodies to spans or structured logs.

## Workflow activities

- `workflow.lifecycle`
- `hosted_agent.invoke`
- `persistence.workflow.add`
- `persistence.workflow.get`
- `persistence.workflow.update`
- `workflow.approval`
- `workflow.recovery`
- `workflow.recovery.abandoned`
- `persistence.approval.record`
- `persistence.support_case.get`

Activity duration is recorded by OpenTelemetry. Completion logs additionally include
workflow ID, workflow trace ID, outcome, and elapsed milliseconds.

## Application Insights queries

Find every custom span for one durable workflow, including separate approval requests:

```kusto
let workflowId = "<workflow-guid>";
dependencies
| where timestamp > ago(24h)
| where tostring(customDimensions["workflow.id"]) == workflowId
| project timestamp, operation_Id, operation_ParentId, name, duration,
          success, resultCode, customDimensions
| order by timestamp asc
```

Follow one distributed HTTP trace through Web UI, orchestrator, and outbound
dependencies:

```kusto
let operationId = "<application-insights-operation-id>";
union requests, dependencies, traces
| where timestamp > ago(24h)
| where operation_Id == operationId
| project timestamp, itemType, name, operation_Id, operation_ParentId,
          duration, success, severityLevel, message, customDimensions
| order by timestamp asc
```

Find logs and spans for a caller-provided correlation ID:

```kusto
let correlationId = "<correlation-id>";
union dependencies, traces
| where timestamp > ago(24h)
| where tostring(customDimensions["correlation.id"]) == correlationId
    or tostring(customDimensions["CorrelationId"]) == correlationId
| project timestamp, itemType, name, operation_Id, duration, message,
          customDimensions
| order by timestamp asc
```

Review failed workflow operations without exposing request content:

```kusto
dependencies
| where timestamp > ago(24h)
| where name startswith "workflow."
    or name startswith "hosted_agent."
    or name startswith "persistence."
| where success == false
| project timestamp, name, operation_Id, duration, resultCode,
          workflowId=tostring(customDimensions["workflow.id"]),
          errorType=tostring(customDimensions["error.type"]),
          outcome=tostring(customDimensions["outcome"])
| order by timestamp desc
```

## Correlation verification

1. Submit a dispute through the Web UI with a unique `x-correlation-id`, such as
   `demo-2026-07-31-01`.
2. Record the returned workflow ID and workflow trace ID.
3. Approve the workflow and confirm a support case is displayed.
4. Run the workflow-ID query. Confirm planner and dispute Hosted Agent spans,
   persistence updates, approval, and `persistence.approval.record` are present.
5. Run the correlation-ID query. Confirm Web UI and orchestrator telemetry share the
   supplied value and outbound dependencies use the same W3C trace operation.
6. Inspect span attributes and logs to confirm the submitted message, approval reason,
   evidence names/content, tokens, and transaction details are absent.

The application test
[`WorkflowTelemetryTests`](../tests/BankingAgent.Application.Tests/WorkflowTelemetryTests.cs)
performs the same lifecycle assertion with explicit PII markers and fails if those
values appear in emitted span attributes. Run it with `task test:application`.
