---
description: How a pipeline may reach its parent, a sibling or a child tenant, the invariant, the abuse cases and the one guard that enforces them.
applies_to: src/MeshAdapter.Sdk/Services/CrossTenant/**, src/MeshNodes.Sdk/Common/TenantTargetConfiguration.cs
---

# Cross-tenant access within a tenant subtree

A pipeline is registered in one tenant and, by default, reads and writes only that tenant. This page describes how a
data node may reach **another** tenant of the same subtree (its parent, a sibling or a child), which rules decide it
and the one guard in the adapter that enforces them. It is the reference for every node that gets a target-tenant
setting and for every review of the guard.

## 1. Starting point

| Fact | Where |
|---|---|
| A pipeline is bound to the tenant of its registration; the context holds exactly one `ITenantRepository`. | `Services/MeshContextCreatorService.cs`, `Services/MeshEtlContext.cs` |
| Nodes open sessions through `IMeshEtlContext.GetSessionForAsync(identity)` and query `etlContext.TenantRepository`. | e.g. `Nodes/Extract/GetRtEntitiesByTypeNode.cs`, `Nodes/Load/ApplyChangesNode2.cs` |
| `ApplyChanges@1` always writes as the system context, by decision. | `Nodes/Load/ApplyChangesNode.cs` |
| Node identity `Caller` = verified caller, else the pipeline's service account, else the system context. `ServiceAccount` = service account, else the system context. | `Services/PipelineIdentityResolver.cs`, [pipeline-execution-identity.md](pipeline-execution-identity.md) |
| An `RtSecurityContext` is a subject plus **role names**, without a tenant. Role names are equal across tenants. | `Runtime.Contracts` (CK engine) |
| The platform tenant registry (system database) stores `ParentTenantId` for every registered tenant; `ISystemContext.GetAllTenantsAsync` returns it. | CK engine MongoDB |
| The identity service offers an RFC 8693 token exchange for a **user** subject token: the issued token is for the target tenant, its subject is the shadow user of the target and its roles come only from explicit provisioning in the target. Sibling grants, allow-lists, second-factor evidence and the tenant generation are checked there. | octo-identity-services |
| Every access token carries `tenant_gen`, the generation of the tenant in `tenant_id`; the current generation comes from the tenant lifecycle store (`ITenantGenerationReader`). | octo-identity-services, octo-common-services |
| The pipeline YAML deserializer is strict: an unknown property fails the registration. | octo-communication-sdk |

## 2. Invariant and relations

### 2.1 Invariant

> A pipeline registered in tenant **T** reads or writes tenant **U ≠ T** only if (1) U is in the relation the node
> names, computed from the platform tenant registry at execution time, and (2) the executing identity holds the
> needed right **in U**, proven by a credential issued for U and evaluated by U's own roles. Otherwise the node fails
> with the explicit error `CROSS_TENANT_FORBIDDEN` and no data of U is read or written. The system context is never
> accepted for U. Without a target-tenant setting a node behaves exactly as before.

Corollaries every implementation keeps:

- **Roles never cross a tenant boundary.** A security context resolved for T is never passed to U's repository: role
  names are equal across tenants, so this would be a confused deputy. The guard opens U's session only with a context
  proven for U.
- **One door.** The guard (`ICrossTenantAccessGuard`) is the only adapter code that opens another tenant's
  repository. `CrossTenantCallSiteArchitectureTests` fails on any new `ISystemContext.Find*Tenant*` call outside its
  allow list.
- **Relation and right are both required.** The relation narrows what identity would allow; the identity proof is
  the authority. Neither replaces the other.
- **Fail closed.** An unreadable registry, an unreachable identity service, a missing generation or a proof that
  cannot be checked is a refusal, never a pass.

### 2.2 Relations

Let `parent(T)` be T's registry parent. The relations are evaluated on the registry, never on identity providers or
data. Version 1 offers three, each named explicitly in the node:

| Relation | U is | Example (G with children A and B) |
|---|---|---|
| `Parent` | `parent(T)` | A → G |
| `Sibling` | a registered child of `parent(T)`, U ≠ T | A → B |
| `Child` | a registered direct child of T | G → A |

Always refused:

- the system tenant as source **or** target (its children are the whole installation); for the same reason two
  children of the system tenant are **not** siblings;
- ancestors above `parent(T)`, nephews and cousins, and any descendant deeper than a direct child;
- tenants outside the registry (also a detached child whose database still exists);
- T itself through the cross-tenant path (the own tenant is the unchanged default path).

A node names the relation, so a pipeline that wants its parent cannot be steered to a sibling by data. Deeper
descendants are not offered in version 1.

## 3. Identity model per node identity

The adapter proves in-process that the executing identity holds a right in U by asking the identity service for a
credential **issued for U**. The guard builds the `RtSecurityContext` for U only from that credential: its subject
and its roles, after checking `tenant_id == U` and `tenant_gen` == U's current generation. The identity service stays
the single authority (grants, allow-lists, second factor, lockout, provisioning, generation and audit).

Rejected alternatives: reading U's grants, mappings or shadow users from MongoDB in the adapter. That would be a
second implementation of the identity service's authorization that drifts from it and is not audited there.

| Node identity | Proof for U | Context in U | Refused when |
|---|---|---|---|
| `Caller` with a verified **user** caller | RFC 8693 exchange of the caller's access token with `acr_values=tenant:U`, sent by the adapter's own client to its own issuer. | The shadow user of U with U-resolved roles. | No caller token, exchange denied, `tenant_id ≠ U`, generation mismatch. |
| `Caller` **without** a caller (schedule, watch, event triggers) | Falls back to the pipeline's service account, handled as `ServiceAccount`. | as below | The fallback would end at the system context → refused. |
| `ServiceAccount` | Token exchange for the service-account subject in the identity service: a shadow client of T's account in U whose roles come only from provisioning in U (same grant and allow-list rules as users; mirrored confidential clients refused; generation checked). | That shadow client with its U roles. | No service account (fallback would be the system context), exchange denied, `tenant_id ≠ U`, generation mismatch. |
| `System` | none | — | **Always refused** for U, at deploy time (controller) and at run time (guard). There is no named exception. |

The `Caller → ServiceAccount → System` fallback stops **before** the system context for every cross-tenant access.

Proofs are cached per execution only (a new execution proves again), never written into `IEtlContext.Properties`,
the data context or the process-wide `IServiceClientAccessToken`, and never logged. The guard keeps no token: it keeps
only the subject and the roles the token carries.

The adapter codes the proof against `ICrossTenantIdentityProvider`. The default implementation performs the user
exchange; the service-account exchange is wired once the identity service offers it, and until then the
service-account path is refused (`identity_not_provable`).

## 4. Node configuration

### 4.1 Shape

One optional property on the data nodes that may cross tenants, a record in `src/MeshNodes.Sdk/Common/TenantTargetConfiguration.cs`:

```csharp
public enum TenantRelation { Parent, Sibling, Child }

public record TenantTargetConfiguration
{
    public required TenantRelation Relation { get; init; }
    public string? TenantId { get; init; }        // Sibling/Child: the target; Parent: not allowed
    public string? TenantIdPath { get; init; }    // alternative to TenantId; value from the data context
}
```

| Node | New property |
|---|---|
| `GetRtEntitiesByType@1`, `GetRtEntitiesById@1`, `GetRtEntitiesByWellKnownName@1` | `targetTenant` (read) |
| `ApplyChanges@2` | `targetTenant` (write), later a fan-out for the master-data sync |
| `ApplyChanges@1`, every other node | none: a target key there is a deserialization error |

The read and write nodes are wired by their own changes; until then only a test node uses the guard.

```yaml
# A reads contacts of its group (parent)
- type: GetRtEntitiesByType@1
  ckTypeId: Basic/Contact
  identity: Caller
  targetTenant:
    relation: Parent
  targetPath: $.contacts

# A writes an incoming document into the addressee company, taken from the invoice
- type: ApplyChanges@2
  identity: ServiceAccount
  entityUpdatesPath: $.updates
  targetTenant:
    relation: Sibling
    tenantIdPath: $.invoice.recipientTenantId
```

A multi-tenant read and a fan-out write name a set of children and decide per refused tenant with
`onRefused: Fail | Report` (default `Fail`); a fan-out sync writes every allowed target, then fails the execution and
names the refused target.

### 4.2 Backwards compatibility

- No property = `null` = the unchanged code path (the node keeps calling `etlContext.GetSessionForAsync` and
  `etlContext.TenantRepository`).
- The strict deserializer works in our favour: an adapter that does not know `targetTenant` refuses the pipeline at
  registration, so an old adapter can never run a cross-tenant pipeline against its own tenant.
- `ApplyChanges@1` gets no property; a pipeline that adds one fails registration.

### 4.3 Relative references and data-driven targets

- The relation is always static configuration; only the tenant id of a `Sibling` or `Child` target may come from data
  (`tenantIdPath`). `Parent` takes no id; a `Parent` with an id, a `Sibling`/`Child` without one, or both `tenantId`
  and `tenantIdPath` is a configuration error.
- A data-driven id is checked like any other: same relation check, same identity proof, same error.
- Tenant ids are trimmed and compared case-insensitively, as in the identity service.

### 4.4 Deploy-time gate (communication controller)

Two rules in `PipelineElevationInspector`:

1. `identity: System` on a node with `targetTenant` (or a set of targets) → deploy refused.
2. `tenantIdPath` is a target-selecting property: an elevated node (`identity: ServiceAccount`) whose target tenant
   comes from a caller-controlled root (`$.body`, `$.query`, `$.files`, `$.headers`, …) is refused like every confused
   deputy.

The run-time guard enforces both again; the deploy gate only reports early.

## 5. Abuse cases

Each row is a test (`CrossTenantAccessGuardTests` with a fake proof provider, `CrossTenantAccessGuardIntegrationTests`
with real tenants in MongoDB, and the controller's inspector tests). "Forbidden" = `CROSS_TENANT_FORBIDDEN`, no read,
no write, and one log line with source, target, relation, identity mode and reason code (never a token).

| # | Abuse case | Expected result | Reason code |
|---|---|---|---|
| 1 | Target outside T's subtree (another group's company) | forbidden | `relation_not_allowed` |
| 2 | Target is a detached child (registry row gone, database kept) | forbidden; same message as "not registered" | `target_not_registered` |
| 3 | Target id from pipeline input data (`tenantIdPath`) pointing anywhere | checked like 1, 2 and 4 | as applicable |
| 4 | Sibling B without a grant for A's identity | forbidden (identity refuses the exchange) | `identity_not_provable` |
| 5 | Node identity `System` with a target | refused at deploy and at run time | `system_identity_refused` |
| 6 | `Caller` without caller and without service account (fallback would be the system context) | forbidden | `system_identity_refused` |
| 7 | A proof for another tenant (token `tenant_id ≠ U`), or a system context returned as proof | forbidden; never applied to U's or T's repository | `identity_not_provable` |
| 8 | Recreated tenant id: U deleted and created again under the same id | the proof's generation ≠ U's current generation → forbidden | `tenant_generation_mismatch` |
| 9 | `ApplyChanges@1` with a target setting | pipeline registration fails (unknown key) | — (deserialization) |
| 10 | Role-name carry-over: T's context used against U | impossible by construction: U's session uses only the U-proven context | — |
| 11 | System tenant as source or as target; two top-level tenants as "siblings" | forbidden | `relation_not_allowed` |
| 12 | Ancestor above the parent, nephew or cousin, grandchild | forbidden | `relation_not_allowed` |
| 13 | Relation `Parent` with an id, `Sibling`/`Child` without one, both id and path | configuration error before any lookup | `invalid_target_configuration` |
| 14 | Hidden attributes evaluated against T's CK model while reading U | the guard loads U's CK cache and the access names U's tenant id, so the attribute guards run against U | — |
| 15 | Generic-create bypass: T's method-invocation marker used for inserts in U | the marker is never carried to U | — (existing guard) |
| 16 | Identity escalation by writing U's identity data | refused cross-tenant, also with a valid grant (write node) | `identity_owned_write` |
| 17 | Cross-tenant association (entity in U associated to an RtId of T) | refused (write node) | `cross_tenant_association` |
| 18 | Second-factor user: caller token without second-factor evidence where U demands it | the identity service refuses → forbidden | `identity_not_provable` |
| 19 | Locked-out or disabled source user | the identity service refuses → forbidden | `identity_not_provable` |
| 20 | Registry, identity service or generation store unreachable | forbidden (fail closed), execution failed | `target_unverifiable` |
| 21 | Enumeration: probing which tenant ids exist | "not registered", "outside relation" and "no right" give the same message; the reason code goes to the log only | — |
| 22 | Proof reuse across executions or callers | proofs are cached per execution only | — |
| 23 | Watch trigger in T on U's data | not offered; triggers stay on the own tenant | — |
| 24 | Pool adapter (no own tenant) executes T's pipeline | the relation is computed from T (the registration's tenant), never from the adapter's tenant | — |

Rows 9 and 15–17 belong to the write node and are tested there; row 23 is a non-feature.

## 6. Implementation

### 6.1 Types (`src/MeshAdapter.Sdk/Services/CrossTenant/`)

| Type | Purpose |
|---|---|
| `ITenantRegistryReader` / `TenantRegistryReader` | reads `ISystemContext.GetAllTenantsAsync` with an admin session into a `TenantRegistrySnapshot` (tenant → parent); cached 30 s, refreshed on a miss at most once per second; throws on an unreadable registry |
| `TenantRelationRules` | pure function: is U in T's relation, and if not, why |
| `ICrossTenantIdentityProvider` / `IdentityServiceCrossTenantIdentityProvider` | proves the identity in U and returns a `CrossTenantProof` (tenant, generation, security context); no token leaves it |
| `ICrossTenantAccessGuard` / `CrossTenantAccessGuard` | the one door: configuration → identity mode → relation → proof → generation → U's repository and CK cache → `ICrossTenantAccess` |
| `ICrossTenantAccess` | what a node uses: `TenantId`, `Repository`, `OpenSessionAsync()` (only with the U-proven context; no system session) |
| `CrossTenantAccessDeniedException` | error code `CROSS_TENANT_FORBIDDEN`, reason code (log only), node path |

`IMeshEtlContext.OpenTenantAsync(target, identity, dataContext, nodeContext)` is a default interface member that
refuses, so any other implementer keeps compiling and fails closed; `MeshEtlContext` delegates to the guard with the
per-execution caller, caller token, identity resolver and proof cache.

### 6.2 Guard algorithm

1. Check the configuration (relation and id combination).
2. Identity `System` → refused.
3. T is the registration's tenant (`etlContext.TenantId`); resolve U: `Parent` → `parent(T)`; else `TenantId` or the
   string at `TenantIdPath`; trim.
4. Relation check on the registry snapshot; a target that is not registered triggers one refresh of the snapshot.
5. Effective identity: `Caller` with a verified caller → user proof with the caller's token; otherwise the pipeline's
   service account → service-account proof; no service account (system context) → refused.
6. Proof by `ICrossTenantIdentityProvider`; check `tenant_id == U`, a non-system context, and `tenant_gen` == U's
   current generation (one refresh of the generation on mismatch).
7. Open U's repository, load U's CK cache, return the access object.
8. One structured log line (information on success, warning on refusal) and the counter
   `octo.mesh_adapter.cross_tenant.decisions` (source, target, relation, mode, outcome, reason).

### 6.3 Writes and synced master data

- An entity synced from a source tenant carries the System CK record `SyncOrigin` (source tenant, source RtId, sync
  time). Idempotency key = source tenant + source RtId; a re-run produces exactly one copy.
- The write guard refuses local edits of an entity whose `SyncOrigin` names another tenant, except by the sync itself;
  entities without `SyncOrigin` are never touched by the sync.
- Every target is written in its own transaction; a failed target fails the execution and names the target, the other
  targets stay written.
- The sync pipeline runs in the source tenant (watch trigger on its own data, `ApplyChanges@2` with a set of children
  and `identity: ServiceAccount`).
- The same `SyncOrigin` contract is reused by writers to other targets (e.g. an external database).

## 7. Tests

- Unit: `CrossTenantAccessGuardTests` (one test per abuse case with a fake registry, proof provider and generation
  reader), `TenantRelationRulesTests`, `IdentityServiceCrossTenantIdentityProviderTests` (scripted token endpoint).
- Integration: `CrossTenantAccessGuardIntegrationTests` with real MongoDB (Testcontainers): G with children A and B,
  an outsider X under another parent, a detached child D and a grandchild; a test node opens the target through the
  guard and reads marker entities.
- Architecture: `CrossTenantCallSiteArchitectureTests` lists every `ISystemContext.Find*Tenant*` call site; a new one
  fails unless it is added to the allow list with a reason.
