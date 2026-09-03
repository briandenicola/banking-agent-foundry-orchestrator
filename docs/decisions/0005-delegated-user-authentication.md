# ADR 0005 — Sign the user in inside the Web UI rather than exchanging an Easy Auth token

- **Status:** Accepted
- **Date:** 2026-09-03
- **Supersedes:** the on-behalf-of design this ADR originally recorded, which was implemented, deployed, and found to be unrunnable in the target subscription
- **Relates to:** [`docs/implementation-backlog.md`](../implementation-backlog.md) item 16, issues [#30](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/30) and [#40](https://github.com/briandenicola/banking-agent-foundry-orchestrator/issues/40)

## Context

The orchestrator needs to know *which customer* a request is for, from something
stronger than a value the Web UI asserts in a header. Today the customer
identifier travels as data the orchestrator trusts, so any caller that reaches
the orchestrator can name any customer.

The obvious answer was on-behalf-of: the Web UI takes the user's token and
exchanges it for one addressed to the orchestrator. That design was written,
implemented, tested, and deployed. It does not work here, for a reason that is
worth recording because it is invisible until you look for it.

### Why on-behalf-of cannot run in this subscription

OBO needs an incoming user token to exchange. Behind Container Apps built-in
authentication ("Easy Auth") the only way to get one is the **token store**, and
the token store has exactly one supported backing configuration: a blob
container addressed by a **shared-access-signature URL**. There is no
identity-based alternative — no managed identity, no user-assigned credential.

This subscription's policy sets `allowSharedKeyAccess = false` on storage
accounts and reverts any attempt to change it. `az storage account update` even
returns success; the value simply stays `false` afterwards. Public network access
is separately disabled and equally immovable. So the SAS is issued fine, is
written into the auth configuration fine, and then returns
`403 KeyBasedAuthenticationNotPermitted` on first use.

The failure is silent in the worst way. Sign-in still succeeds, the application
looks healthy, and the only symptom is that `/.auth/me` returns `[]` and the
`X-MS-TOKEN-AAD-ACCESS-TOKEN` header never appears. It took a direct `curl`
against the SAS to see the 403 at all.

## Decision

**Sign the user in inside the Web UI** with `Microsoft.Identity.Web`, using the
OpenID Connect authorization-code flow, and acquire orchestrator tokens with
`ITokenAcquisition.GetAccessTokenForUserAsync`. The orchestrator continues to
validate a delegated user token exactly as designed; only the way the Web UI
obtains that token changes.

The whole path stays behind a flag, now `enable_user_delegation`, **off by
default**. With the flag off the deployment is unchanged: Easy Auth in front, the
customer identifier passed onward as an assertion.

### This is not on-behalf-of, and it should not pretend to be

OBO exists for a middle tier that receives a token it did not request and needs a
different one. A web application that runs its own sign-in is not in that
position: it holds the authorization code and the refresh token from its own
login, so it can ask for the orchestrator's scope directly. That is Microsoft's
documented "web app calls a web API" pattern, and `GetAccessTokenForUserAsync`
implements it.

Naming matters here because the earlier design's justification was that the
exchange "makes the identity chain demonstrable". It still is: the orchestrator
receives a token issued *to the Web UI, for this user*, with the orchestrator's
own audience, and Entra can say so. The token is obtained by redeeming a refresh
token rather than by exchanging an assertion. The security property is the same;
the round trip that was going to prove it is gone, and so is the ADR's original
claim that it was necessary.

## Consequences

**The client secret stays, and the earlier reasoning for it still holds.** The
Web UI is a confidential client and must prove it is the registered application.
A federated identity credential would avoid the secret, but the registration
lives in the sign-in tenant (the only tenant where the operator may create an
`api://` identifier URI — that is issue #30) while the managed identity is an
Azure resource in the deployment tenant, so the credential would have to trust a
cross-tenant issuer. That is not a configuration to discover during a demo. Every
service-to-service call the application makes *on its own behalf* still uses
managed identity; the secret authenticates the client application in a delegated
user flow, which is a different thing.

**One key at rest disappears.** The storage account and blob SAS the token store
required are gone, along with the two-pass Terraform apply their unknown-at-plan
value forced. The client secret is now the deployment's only key.

**The Web UI needs its own redirect URIs**, `/signin-oidc` and `/signout-oidc`.
The `api://<webui>/access_as_user` scope that OBO required is no longer used;
nothing breaks if it is left in place.

**Sign-out clears both schemes.** Dropping only the local cookie would leave the
Entra session intact, so the next sign-in would complete silently and the user
would appear unable to sign out.

**A revision restart forces re-sign-in.** Both the token cache and the data
protection key ring are in-process, because a distributed cache needs a backing
store and every store available here is either a key at rest or blocked by the
same policy that ruled out the token store. The cost is one interactive sign-in
after a restart. A PostgreSQL-backed key ring is the way out if that becomes
unacceptable; it was deferred because the Web UI has no database access today.

**Delegation covers the interactive path only.** Workflows resume in the
background through the recovery worker, long after any user token has expired.
That path keeps the existing model, where the orchestrator asserts the customer
identifier recorded on the workflow. Persisting refresh tokens to bridge the gap
was rejected — it would put user credentials at rest in the workflow store.

**It stops at the orchestrator.** The user token is not carried onward to
Foundry. The Foundry data plane authorises on Azure RBAC, so a delegated token is
evaluated against the *user* principal and would require every bank customer to
be a principal in the bank's Foundry tenant with a Foundry role. Foundry calls
continue to use the orchestrator's managed identity with the customer's object ID
asserted as a memory scope.

**It is mutually exclusive with `enable_service_auth`.** Both configure the same
JWT bearer scheme with different issuers and audiences, so whichever won would
reject the other's callers. Terraform fails the plan on a precondition and the
orchestrator refuses to start, rather than deploying a combination that rejects
every request at runtime.

**Revisit if the storage policy or issue #30 changes.** Neither would bring OBO
back — in-app sign-in is the better shape regardless — but resolving #30 puts the
registration and the managed identity in one tenant, at which point a federated
identity credential becomes ordinary configuration and the secret should go.
