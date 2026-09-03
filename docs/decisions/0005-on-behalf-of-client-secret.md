# ADR 0005 — Use a confidential-client secret for the on-behalf-of exchange

- **Status:** Accepted
- **Date:** 2026-09-03
- **Relates to:** [`docs/implementation-backlog.md`](../implementation-backlog.md) item 16, issues [#30](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/30) and [#40](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/40)

## Context

The repository's standing rule is that service-to-service authentication uses
Entra ID with managed identity or workload identity, never an API key or a
client secret. On-behalf-of breaks that rule, and the backlog item that proposed
OBO said so and asked for a decision before anyone implemented it. This is that
decision.

OBO exchanges the user's access token for a second token addressed to a
downstream API. The exchange is a confidential-client call: the caller must
prove it *is* the registered client application, not merely that it is running
in Azure. Two credential types can do that — a client secret or certificate, and
a federated identity credential (FIC) that lets an external issuer's token stand
in for the secret.

The FIC path is the one that would preserve the no-keys rule, so it was
evaluated first. It does not work here:

- The Web UI's app registration lives in the **sign-in tenant** (tenant B in this
  deployment), because that is the only tenant where the operator is permitted to
  create an `api://` identifier URI at all. That constraint is the whole reason
  issue #30 is open.
- The Web UI's managed identity lives in the **deployment tenant** (tenant A),
  because a managed identity is an Azure resource and cannot be created anywhere
  else.
- A federated credential therefore has to trust an issuer in a different tenant
  from the registration. Azure managed identity as a cross-tenant FIC issuer is
  not a documented or verified configuration, and a demo deployment is the wrong
  place to discover whether it works.

So the realistic options were: a client secret, or no OBO.

## Decision

Use a client secret for the OBO exchange, reusing the secret Easy Auth already
requires for the same registration, and confine the whole feature behind
`enable_obo`, which is **off by default**.

Three properties make this narrower than it sounds:

1. **No new secret exists.** Easy Auth already requires a client secret for the
   Web UI registration, and it is already stored as a Container Apps secret. OBO
   reuses that exact secret rather than introducing a second credential, so the
   number of keys in the deployment does not change.
2. **The rule it bends is about service identity, and that is unchanged.** Every
   service-to-service call the application makes on its own behalf — to Foundry,
   to PostgreSQL, to the registry — still uses managed identity. The secret
   authenticates the *client application* in a delegated user flow, which is a
   different thing from a service authenticating itself.
3. **It is off unless a deployment asks for it.** With `enable_obo = false` the
   secret is used only by Easy Auth, exactly as before this ADR.

## Alternative rejected: request the orchestrator's scope at sign-in

Easy Auth can be told to request any scope at login. Pointing `loginParameters`
straight at `api://<orchestrator>/user_impersonation` would put an
orchestrator-audience token in the token store, and the Web UI could forward it
verbatim. No exchange, no MSAL, no confidential-client call — the client secret
would go back to being Easy Auth's business alone.

It was rejected because it is not on-behalf-of, and the difference is the point
of the feature. Forwarding hands the browser's own token through unchanged: the
Web UI becomes a pipe rather than a party to the transaction, and there is no
step at which the middle tier authenticates itself. The exchange is what makes
the identity chain demonstrable — the orchestrator's token was issued *to the
Web UI, for this user*, and Entra can say so.

The cost of doing it properly is one more piece of configuration: the Web UI has
to expose an API of its own (`api://<webui-client-id>/access_as_user`) and
request that scope at sign-in, because OBO requires an assertion whose audience
is the middle tier and Entra will not issue an access token for an application
that exposes no scope.

## Consequences

**A second key at rest appears with the feature.** Enabling OBO also enables the
Easy Auth token store, because Container Apps only injects
`X-MS-TOKEN-AAD-ACCESS-TOKEN` when the store is on, and there is nothing to
exchange without that header. The store is configured with a blob SAS URL, and
Container Apps offers no identity-based alternative. So `enable_obo = true`
provisions a storage account and a SAS. The SAS is scoped to one private
container that holds nothing but Easy Auth's own session tokens, and it expires;
it is still a key, and `apps/webui-obo.tf` says so plainly.

**OBO covers the interactive path only.** Workflows resume in the background
through the recovery worker, long after any user token has expired. That path
keeps the existing model, where the orchestrator asserts the customer identifier
recorded on the workflow. Persisting refresh tokens to bridge the gap was
rejected — it would put user credentials at rest in the workflow store.

**OBO stops at the orchestrator.** It is not carried onward to Foundry. The
Foundry data plane authorises on Azure RBAC, so a delegated token is evaluated
against the *user* principal and would require every bank customer to be a
principal in the bank's Foundry tenant with a Foundry role. Foundry calls
continue to use the orchestrator's managed identity with the customer's object
ID asserted as a memory scope.

**It is mutually exclusive with `enable_service_auth`.** Both configure the same
JWT bearer scheme with different issuers and audiences, so whichever won would
reject the other's callers. Terraform fails the plan on a precondition and the
orchestrator refuses to start, rather than deploying a combination that rejects
every request at runtime.

**Revisit if cross-tenant federated credentials become viable.** The decision
here is driven by a tenant constraint, not by a preference for secrets. If the
`api://` identifier URI becomes available in the deployment tenant — that is, if
issue #30 is resolved — the registration and the managed identity land in the
same tenant and a federated identity credential becomes an ordinary
configuration. At that point the secret should go.
