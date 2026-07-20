# MCP authentication for pipeline nodes — design & recommendation

Status: proposed
Date: 2026-07-02
Scope: `octo-mesh-adapter` (`McpToolCall@1`, `LlmQuery@1`, `McpServerResolver`), with coordination points in `octo-identity-services` (WI #4208) and platform-services.

## Context

`LlmQuery@1` (agentic) and the new `McpToolCall@1` (deterministic) both reach MCP servers
through the shared `McpServerResolver`, which today composes auth from two static
`McpConfiguration` fields: `BearerToken` (→ `Authorization: Bearer …`) and `AdditionalHeaders`
(custom header map, e.g. Exa `x-api-key`). That covers static API keys but not OAuth2.

The near-term driver is the **MeshMakers MCP server** (`octo-mcp-service`) — an HTTP+SSE MCP
exposing ~177 tools (the full `octo-cli` surface + CK CRUD + asset-repo query APIs). We want it
available to pipelines **by default**, and unattended (no human in the loop). A second driver is
Microsoft **Work IQ** (SharePoint/Teams), which is Entra-protected.

### Confirmed facts (2026-07-02)

- **`octo-mcp-service` accepts standard inbound JWT Bearer.** Its `Program.cs` registers
  `ConfigureJwtBearerOptions`; the `authenticate`/`check_auth_status` device-flow tools are only a
  convenience for interactive clients that can't set headers. So a **client-credentials token sent
  as `Authorization: Bearer` is validated directly** — no device flow needed for a pipeline node.
  Endpoints: `/{tenantId}/mcp` and `/mcp` (local `http://localhost:5016/mcp` /
  `https://localhost:5017/{tenantId}/mcp`; remote e.g. `https://mcp.test-2.mm.cloud/mcp`).
  Required token: scope `OctoApiFullAccess` or `OctoApiReadOnly` + the `allowed_tenants` claim.
- **`octo-identity-services`** is the Octo IdP (Duende), OAuth2/OIDC, multi-tenant, and can
  federate to external IdPs incl. **Azure Entra ID** (user login federation).
- **The auth mechanism already exists in the adapter.** `IServiceAccountTokenService`
  (`MeshAdapter.Sdk/Services/ServiceAccountTokenService.cs`) reads a
  `System.Communication/ServiceAccountConfiguration` entity by well-known name
  (`IssuerUri`, `ClientId`, `ClientSecret`, `TenantId`), does OIDC discovery on `IssuerUri`,
  requests a **client-credentials** token (scope `OctoApiFullAccess`, `acr_values=tenant:{TenantId}`),
  and caches it with a 60s expiry buffer. `DeployPipelineNode@1` already consumes it via a config
  field `ServiceAccountConfigName` (default `"ServiceAccountConfig"`).

## Decision

Adopt **client-credentials (app-only) auth** for pipeline → MCP calls, reusing the existing
`ServiceAccountConfiguration` CK type and `IServiceAccountTokenService`. Device flow is
interactive-only and is explicitly **not** used by nodes. Delegated / on-behalf-of (per-user) is
out of scope for unattended pipelines.

The four sub-decisions below were evaluated; the recommended option is marked **✓**.

## #1 — What this means for Work IQ (Entra)

Same *mechanism* (client-credentials → Bearer), different *authority + scope*.
`ServiceAccountTokenService` already takes `IssuerUri` from the config, so it can point at the
Entra authority. Two gaps for Work IQ:

- It **hardcodes** the Octo scope (`OctoApiFullAccess`) and adds `acr_values=tenant:` — both
  Octo-specific. Entra needs a resource scope (e.g. `<workiq-app>/.default`) and **no** `acr_values`.
- Work IQ needs an **Azure app registration** (client-credentials) with Work IQ permissions; its
  clientId/secret + Entra authority live in a `ServiceAccountConfiguration`.

Conclusion: Work IQ is reachable with a **small generalization** — make `Scope` a config attribute
and `acr_values` optional — not a new auth stack. The Octo IdP's Entra *federation* is for user
login and does **not** mint app-only Work IQ tokens, so it does not substitute for the app
registration.

## #2 — Where the auth reference lives

Reuse the `ServiceAccountConfiguration` CK type and the `ServiceAccountConfigName` naming
convention already established by `DeployPipelineNode@1`.

- **2a (interim, no codegen):** add `ServiceAccountConfigName` to the *node* config
  (`McpToolCallNodeConfiguration`, optionally `LlmQueryNodeConfiguration`). Fast to prove out; auth
  is specified per caller.
- **2b ✓ (recommended end state):** add a `serviceAccountConfigName` attribute to the
  **`McpConfiguration` CK type**, so auth belongs to the *server definition* and every caller
  (`LlmQuery`, `McpToolCall`) picks it up automatically. This is what makes "MeshMakers MCP by
  default" clean — the seeded config carries its own auth. Cost: one CK-model bump + blueprint +
  codegen (the known cycle).

Recommendation: ship **2b** for the real thing; use **2a** only as a smoke test before paying the CK
cost. When `serviceAccountConfigName` is empty, behaviour is unchanged (static `BearerToken` /
`AdditionalHeaders`).

## #3 — Where token acquisition runs

Reuse `IServiceAccountTokenService` (already DI-registered and battle-tested via `DeployPipelineNode`).

- **Promote `McpServerResolver` from a static class to an injected DI service** and inject
  `IServiceAccountTokenService` into it, so `LlmQuery` and `McpToolCall` get auth in one place.
- Add a method that **returns the access-token string** for a given `ServiceAccountConfigName`.
  Today the service writes into the shared `IServiceClientAccessToken` (meant for the SDK service
  client); reusing that holder for MCP risks clobbering a concurrent `DeployPipeline` call, so a
  "give me the token" overload is cleaner. The resolver injects the returned token as
  `Authorization: Bearer` on the MCP transport when `serviceAccountConfigName` is set, else falls
  back to the static `BearerToken` / `AdditionalHeaders`.
- Wiring detail to confirm at implementation time: how the node obtains the `ITenantRepository`
  that `EnsureTokenAsync` needs — `DeployPipelineNode` already does this; mirror it.

## #4 — Client registration + default seed

- **Coordinate with WI #4208** (`dev/4208-mcp-clients-in-bootstrap-blueprint` on
  `octo-identity-services`) for the OIDC **client** registration — that is the client-credentials
  client whose creds populate the `ServiceAccountConfiguration`. Do not duplicate it.
- **Seed** the `ServiceAccountConfiguration` + a default `McpConfiguration` (pointing at the
  MeshMakers MCP `/{tenantId}/mcp`, referencing that service account) via the **System.Communication
  blueprint** (proven), unless platform-services is ready to own blueprint/CK seeding — check with
  Gerald, since platform-services is taking over CK/blueprint import.
- **Security:** scope the default MCP client tightly (`OctoApiReadOnly` unless it truly needs
  admin). The MeshMakers MCP exposes tenant-admin / CK / pipeline tools, so a broad default is
  dangerous. Tighten `allowed_tenants` as well.

## Net recommendation

Option A is mostly *wiring existing parts*:

1. Reuse `IServiceAccountTokenService`.
2. Reference it from the MCP config via `serviceAccountConfigName` — **2b**, on the
   `McpConfiguration` CK type (with **2a** on the node config as an optional smoke test first).
3. Acquire tokens in an **injected** `McpServerResolver` (promote from static), adding a
   token-string method.
4. For Work IQ later: add config-driven `Scope` + optional `acr_values` to
   `ServiceAccountConfiguration` / `ServiceAccountTokenService`.

Genuinely new work is small: the CK attribute (2b), the token-string method + resolver-to-service
promotion (#3), and (deferred) the scope generalization for Entra (#1). Everything else already
exists.

## Suggested implementation order

1. Promote `McpServerResolver` to a DI service; inject `IServiceAccountTokenService`; add a
   token-string accessor. (No CK change.)
2. **2a**: add `ServiceAccountConfigName` to `McpToolCallNodeConfiguration`; wire the Bearer path;
   smoke-test against the MeshMakers MCP with a hand-created `ServiceAccountConfiguration`.
3. **2b**: add `serviceAccountConfigName` to the `McpConfiguration` CK type (model bump + blueprint
   + codegen); move the reference there; drop the node-level field or keep as override.
4. Seed the default MeshMakers-MCP `McpConfiguration` + `ServiceAccountConfiguration` via blueprint,
   coordinated with WI #4208.
5. Work IQ (Entra): generalize `Scope` + optional `acr_values`; add an Entra app registration +
   `ServiceAccountConfiguration`; validate against Work IQ SharePoint.

## Open items / coordination

- WI #4208 — MCP OIDC client registration in the identity bootstrap blueprint.
- Platform-services — will it own the blueprint/CK seed for the default MCP config?
- Confirm the exact `ITenantRepository` acquisition in the node (mirror `DeployPipelineNode`).
- Encryption-at-rest for `ClientSecret` / `BearerToken` / secret headers (`enc:v1`) — separate,
  pre-existing backlog item; more relevant once service-account secrets are stored.

## References (verified 2026-07-02)

- `octo-mcp-service/src/McpServices/Program.cs` — `ConfigureJwtBearerOptions`, `MapMcp` endpoints, scopes.
- `octo-mesh-adapter/src/MeshAdapter.Sdk/Services/ServiceAccountTokenService.cs` — client-credentials token acquisition.
- `octo-mesh-adapter/src/MeshNodes.Sdk/Load/DeployPipelineNodeConfiguration.cs` — `ServiceAccountConfigName` precedent.
- `octo-mesh-adapter/src/MeshAdapter.Sdk/Nodes/Transform/McpServerResolver.cs` — current auth composition.
- `octo-identity-services` — Duende IdP; external IdP federation incl. Azure Entra ID.
