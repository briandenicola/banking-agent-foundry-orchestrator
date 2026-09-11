# ADR 0006 — Call Foundry over the Responses API

- **Status:** Accepted
- **Date:** 2026-09-11
- **Relates to:** [ADR 0003](0003-foundry-memory-prompt-agent.md), [ADR 0004](0004-foundry-toolbox-tools.md)

## Context

This system was calling Foundry on two different API surfaces, and nobody had
decided that it should.

The C# `CustomerProfileClient` posts to `/openai/v1/responses`. That was
deliberate: it is a hand-written `HttpClient` and the URL is in the source.

The Python hosted agents were calling `/openai/v1/chat/completions`. That was
not deliberate. `model.py` sets `base_url` to the project's `/openai/v1/`
surface and hands it to LangChain's `ChatOpenAI`, which appends
`chat/completions` by default. The surface was chosen by a library default
that nobody had looked at.

The split was visible in telemetry all along — `task cloud:logs:model` reports
both `completion` and `responses` under `model_usage` — but nothing in `docs/`
mentioned either surface, so there was no record to contradict.

Both surfaces answer on the same base URL and the same Entra scope
(`https://ai.azure.com/.default`), so this is not a question of access. It is a
question of which surface the system is built on.

## Decision

**Hosted agents call Foundry over the Responses API.**
`_use_responses_api()` in `src/agents/python/app/model.py` gates
`ChatOpenAI(use_responses_api=...)` and defaults to enabled.

Responses is the surface Foundry treats as current, and it is where
server-side state — reasoning-item persistence, tool state across turns — is
being added. Standardising there means the C# and Python halves of the system
agree, and future work that needs those features does not have to migrate the
call path first.

Two things were checked against the deployed project before switching, because
both change shape under Responses and neither is covered by mocked tests:

- **`with_structured_output(AgentResult)`.** `AgentResult.selected_agent` is
  optional, and strict JSON-schema mode rejects some schemas that the
  function-calling path accepts. Verified: both surfaces returned the same
  populated `AgentResult` for the same prompt.
- **`bind_tools` in `toolbox.py`.** `_requested_calls` reads
  `response.tool_calls` and requires a list of dicts. Verified: both surfaces
  returned `[{'name': ..., 'args': {...}}]` identically.

The agent code never reads `response.content`, so the content-block
representation that Responses returns is not a concern here. It would be for
any future code that does.

## What was deliberately left alone

**The local `AzureChatOpenAI` branch stays on Chat Completions.** It runs only
against a standalone Azure OpenAI resource for local development, its Responses
path needs a different `api-version`, and the deployed stack never exercises
it. Changing an untested path would let local and deployed behaviour drift
apart without anything failing.

**Prompt agents are unaffected.** `customer-profile` is `kind: prompt`, so
Foundry runs the model loop server-side and the API surface is not ours to
choose. It already appears as `responses` in telemetry.

## Consequences

- The C# client and the Python agents now use one surface.
- `BANKING_AGENT_USE_RESPONSES_API` reverts the Python side without a rebuild.
  It is opt-*out*: only `false`, `0`, `no`, or `off` disable it. The escape
  hatch exists because a specialist that cannot reach its model degrades to
  canned fallback text rather than raising, so a bad surface would show up as
  plausible output rather than an error.
- Verify a deployment with `task cloud:logs:model`. Under `model_usage`,
  `completion` should fall toward zero and `responses` should rise. Because
  the failure mode is silent, that telemetry — not the Web UI — is the check.
- `docs/technical-spec.md` records the surface alongside the model
  configuration.
