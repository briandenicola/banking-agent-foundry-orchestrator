# Functional Specification

## Product goal
Build a banking support agent prototype that can assist with transaction questions and safely carry out approved actions through a multi-step workflow.

## Primary user journey
1. A user submits a request such as "Explain this pending transaction" or "Dispute this charge".
2. The system classifies the request and passes it to the reasoning agent.
3. The action-planning agent determines whether the request is informational or sensitive.
4. If sensitive, the orchestrator pauses and requires explicit approval.
5. After approval, the orchestrator executes the action and records a complete audit trail.

## Core features
- Intent classification and request normalization.
- Two-agent reasoning flow: one agent for understanding and one for planning.
- Approval-required workflow for sensitive actions.
- Structured logs and traces for every workflow step.
- Model access through a LiteLLM gateway for routing and provider abstraction.
- Support for informational responses and bounded actions.

## Example scenarios
- Explain a recent purchase and provide likely reasons.
- Summarize suspicious transactions and recommend next steps.
- Create a support case after a dispute request.
- Handle a dispute initiation only after approval and validation.

## Functional requirements
- The system must accept a user message and return a workflow response.
- The workflow must distinguish between read-only and action-taking intents.
- Sensitive actions must require explicit approval before execution.
- The system must return a trace identifier that links the request, agent decisions, approval steps, and outcome.
- The system must store workflow metadata and audit events for later review.

## Out of scope for v1
- Live bank integrations or real transaction settlement.
- Advanced policy engine beyond simple approval rules.
- Multi-tenant or highly regulated compliance controls.
