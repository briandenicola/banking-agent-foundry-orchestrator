# ADR 0002: Keep the hand-written MCP implementation, verified by the official SDK

- Status: Accepted
- Date: 2026-08-06
- Related: [#23](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/23), [#36](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/36)

## Context

#23 delivered MCP as a vertical slice using a hand-written JSON-RPC 2.0
implementation on both sides: `src/agents/python/app/mcp_server.py` for the
server and `src/infrastructure/FoundryMcpClient.cs` for the client. #36 asks
whether the official MCP SDK should replace it, citing two soft spots:
`protocolVersion` negotiation is nominal, and there is no transport-level
session management.

Two constraints shape the answer.

**The transport is not ours to choose.** Foundry Hosted Agents are reached
through the Foundry invocations endpoint
(`/agents/{name}/endpoint/protocols/invocations?api-version=v1`), which is a
request/response HTTP surface fronted by Foundry's own ingress and Entra
authentication. The SDK ships stdio, SSE, WebSocket, and Streamable HTTP
transports; the only plausible fit here is Streamable HTTP, which assumes the
server owns an addressable route and negotiates session headers
(`mcp-session-id`, `mcp/server/streamable_http.py:51`) and an optional SSE
upgrade.

We have none of that. `hosted.py` does register `Route("/mcp", ...)`, but
Foundry addresses the agent through its invocations protocol rather than that
path, which is why `handle_invoke` sniffs for a `"jsonrpc": "2.0"` body and
replays it into the MCP handler. In other words the MCP messages arrive over
Foundry's invocation channel, not over a route a Streamable HTTP server could
own. Adopting the SDK server would mean adapting it to a transport it does not
model, trading hand-written protocol code for hand-written transport-adapter
code.

**The client is C#, the server is Python.** Even if the Python server moved to
the SDK, the orchestrator would still need an MCP client that authenticates
with a managed-identity bearer token against the Foundry endpoint. That is not
what the SDK's C# client transports assume either.

## Decision

Keep the hand-written implementation on both sides, but stop treating our own
tests as evidence of conformance. Add the official `mcp` Python SDK as a
**test-only** dependency and validate every server response against the SDK's
published Pydantic models (`InitializeResult`, `ListToolsResult`,
`CallToolResult`, `JSONRPCResponse`) in `tests/test_mcp_interop.py`.

This gives the property the SDK was wanted for — independent conformance —
without taking a runtime dependency that fights the transport.

## Consequences

- The SDK is a conformance oracle, not a runtime dependency. It lives in
  `requirements-dev.txt` and is absent from the container image.
- A response shape that our own client tolerates but the ecosystem would reject
  now fails a test. This already constrains all four agents.
- Known limitations are accepted rather than fixed, because the transport makes
  them inert: `protocolVersion` negotiation remains nominal (the server always
  answers `2024-11-05`), and there is no session management, because Foundry
  invocations are stateless request/response with no session to manage.
- Conformance coverage is limited to what the SDK's types express. Behavioural
  conformance (ordering, lifecycle) is not covered by this ADR.

## Revisit if

- Foundry exposes hosted agents over a transport the SDK models directly
  (Streamable HTTP with route and header control), which would make the SDK
  server a straight substitution.
- We need MCP features whose hand-written cost is high — resources, prompts,
  sampling, or notifications — rather than the single-tool surface we expose.
- A third-party client fails against our server in a way the type-level
  conformance tests do not catch, which would show the oracle is too weak.
