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

The adapter codes the proof against `ICrossTenantIdentityProvider`. The default implementation
(`IdentityServiceCrossTenantIdentityProvider`) performs both exchanges at the adapter's own issuer only:

| Subject | Presenter (`client_id`) | `subject_token` | The issued token must carry |
|---|---|---|---|
| User caller | the adapter's own client (secret) | the caller's access token | a `sub` (the shadow user of U) |
| Service account | U's public shadow client `xt_{T}_{clientId}` (no secret, AB#9777) | the account's own verified client-credentials token of T | `client_id` = `xt_{T}_{clientId}` (exact) and no `sub` |

For both, the issued token is verified before a claim is used, with the verifier of the AB#9776 fix
(`Services/IssuedTokenVerifier.cs`): signature against the own issuer's JWKS, `iss` one of the own names, not expired,
`tenant_id` = U. Only its roles reach the session in U; its `tenant_gen` is compared with U's current generation by
the guard. Identity's refusals (`access_denied` with `tenant_not_provisioned`, `grant_not_permitted`,
`mirrored_client`, `ambiguous_subject`, `shadow_client_invalid`; `unauthorized_client`; `invalid_grant`) become
`CROSS_TENANT_FORBIDDEN` (`identity_not_provable`); the log names only the error code and that reason keyword, never a
token or identity's description text. An unreachable identity service is `target_unverifiable`, and so is a service
account that has no token in its own tenant (the token endpoint unreachable, the account refused there): that says
nothing about U, so it is no refusal of U. The service account is
the one the execution acts as (`ServiceAccountConfigurationReader`, shared with the identity resolver); a
configuration naming another tenant is refused (AB#9776) and is never a way into U.

## 4. Node configuration

### 4.1 Shape

One optional property on the data nodes that may cross tenants, a record in `src/MeshNodes.Sdk/Common/TenantTargetConfiguration.cs`:

```csharp
public enum TenantRelation { Parent, Sibling, Child }

public record TenantTargetConfiguration
{
    public required TenantRelation? Relation { get; init; }  // must be named; a missing relation is refused
    public string? TenantId { get; init; }        // Sibling/Child: the target; Parent: not allowed
    public string? TenantIdPath { get; init; }    // alternative to TenantId; value from the data context
}
```

| Node | New property |
|---|---|
| `GetRtEntitiesByType@1`, `GetRtEntitiesById@1`, `GetRtEntitiesByWellKnownName@1` | `targetTenant` (read) |
| `ApplyChanges@2` | `targetTenant` (write: create, update, delete and association changes), later a fan-out for the master-data sync |
| `ApplyChanges@1`, every other node | none: a target key there is a deserialization error |

The read nodes (AB#9768) and `ApplyChanges@2` (AB#9769) open the target only through
`IMeshEtlContext.OpenTenantAsync`, the one guard, and use only the repository and session it returns.

```yaml
# A reads contacts of its group (parent)
- type: GetRtEntitiesByType@1
  ckTypeId: Basic/Contact
  identity: Caller
  targetTenant:
    relation: Parent
  targetPath: $.contacts

# A writes an incoming document into the addressee company, taken from the invoice. A target from data needs a
# verified caller (section 4.3): the user who releases the invoice, proven in the addressee company.
- type: ApplyChanges@2
  identity: Caller
  entityUpdatesPath: $.updates
  targetTenant:
    relation: Sibling
    tenantIdPath: $.invoice.recipientTenantId
```

A multi-tenant read and a fan-out write name a set of children and decide per refused tenant with
`onRefused: Fail | Report` (default `Fail`); a fan-out sync writes every allowed target, then fails the execution and
names the refused target.

### 4.1a Writing into another tenant (`ApplyChanges@2`)

With `targetTenant` set, `ApplyChanges@2`:

1. opens the target through the guard first (section 6.2) — before the write checks, before the dry-run branch and
   before anything is written; a refusal fails the execution with `CROSS_TENANT_FORBIDDEN`, also in a dry run;
2. checks the batch with the **target's** CK model and **without** the run's method marker (`WriteGuardScope`): a
   method run belongs to the pipeline's own tenant, so MethodOnly attributes, inserts of a type whose method replaces the
   generic create and identity association edges are always refused in another tenant (abuse cases 15 and 16);
3. refuses an association change whose end is neither inserted by the same batch nor held by the target (read with
   the session proven in the target, abuse case 17);
4. writes every create, update and delete in one transaction of the target, with the session the guard proved there
   (the engine stamps `RtCreatedBy` with that identity and enforces the target's data permissions for it).

The dry-run intent names the target (`targetTenantId`). `onDuplicateKey`, the write-conflict retries and the merge of
several updates of one entity work as in the own tenant.

```yaml
# Company A's release pipeline (FromHttpRequest@2, run by the releasing user): the released invoice (own tenant)
# becomes an incoming document in the addressee
- type: GetRtEntitiesByType@1
  ckTypeId: Acme.Accounting/InterCompanyInvoice
  targetPath: $.invoice
  fieldFilters:
    - attributePath: InvoiceNumber
      operator: Equals
      comparisonValuePath: $.invoiceNumber
    - attributePath: InvoiceStatus
      operator: Equals
      comparisonValue: Released
- type: CreateUpdateInfo@1
  targetPath: $._inserts
  targetValueKind: Array
  updateKind: INSERT
  ckTypeId: Acme.Accounting/IncomingDocument
  generateRtId: true
  attributeUpdates:
    - attributeName: InvoiceNumber
      attributeValueType: String
      valuePath: $.invoice.Items[0].Attributes.InvoiceNumber
    - attributeName: SourceInvoiceRtId
      attributeValueType: String
      valuePath: $.invoice.Items[0].RtId
- type: ApplyChanges@2
  entityUpdatesPath: $._inserts
  identity: Caller
  targetTenant:
    relation: Sibling
    tenantIdPath: $.invoice.Items[0].Attributes.RecipientTenantId
```

### 4.1b Master-data sync into the child tenants (`ApplyChanges@2` with `targetTenants`)

A group tenant G maintains master data (contacts, for example) once and syncs it one way into its companies. The sync
pipeline runs in G: `FromWatchRtEntity@1` on G's own entities (no cross-tenant trigger, abuse case 23), then
`ApplyChanges@2` with `targetTenants` and `identity: ServiceAccount` (a watch trigger has no caller).

```yaml
- type: ApplyChanges@2
  identity: ServiceAccount
  entityUpdatesPath: $._sync
  targetTenants:
    relation: Child              # the only relation of v1
    tenantIds: [companya]        # optional; without it every registered direct child
```

- **Targets.** Without `tenantIds` the registered direct children of G at execution time (the guard's registry
  snapshot, so a child created seconds ago may be missed for up to 30 s). Each target, listed or not, is opened through
  the guard as a `Child` target: relation, identity proof in that company, generation. A listed id that is no direct
  child (a grandchild, a detached or foreign tenant) is refused like any other target.
- **Owner marker and idempotency.** The copy keeps the source RtId and carries the System record `RtSyncOrigin`
  (`SourceTenantId`, `SourceRtId`, `SyncedAt`; System 2.10.0). The node stamps it itself with the pipeline's tenant
  as source and drops any origin the input carries, so a sync can never claim another source. Per target the node
  reads the stored entities of the batch: no entity → insert, this sync's copy → update, a delete → delete (nothing
  when the copy is gone). Running twice leaves exactly one copy.
- **Local entities are never touched.** An entity of the target under that RtId without `RtSyncOrigin`, or with the
  origin of another source, refuses the whole target (`SYNC_OWNED_ENTITY`); nothing of that target is written.
- **Engine refusal of a single-target write.** With `targetTenant` (one target) an engine validation error in the
  target now fails the execution with `CROSS_TENANT_WRITE_FAILED` naming the target (code `VALIDATION_FAILED`). A write
  into the own tenant keeps its old behaviour: the error is logged and the pipeline goes on.
- **Failure.** Each target is written in its own transaction. A refused or failed target does not stop the others;
  after the last target the node throws `CROSS_TENANT_WRITE_FAILED` naming every failed target with its stable code
  (`CROSS_TENANT_FORBIDDEN`, `SYNC_OWNED_ENTITY`, `ATTRIBUTE_WRITE_RESTRICTED`, `VALIDATION_FAILED`, `WRITE_CONFLICT`,
  `WRITE_FAILED`; never a value) and the written ones. The trigger reports the execution as failed with that message.
- **Local edits of synced data.** The rule lives in the engine (octo-construction-kit-engine-mongodb,
  `SyncOriginWriteRules`), in the write funnel of every tenant repository, so it holds for every writer: GraphQL and
  REST of the asset repository, `ApplyChanges@1` and `ApplyChanges@2` (own tenant or one `targetTenant`), bots, and
  system sessions too. Outside the sync, setting or changing `RtSyncOrigin`, and an update, replace, delete or archive of
  an entity that carries it, are refused (`SYNC_OWNED_ENTITY`; the engine's operation message 144 with `code` =
  `SYNC_OWNED_ENTITY` names the entity and the reason, the write nodes map it to `SyncOriginWriteRefusedException`). The
  data is changed in G and reaches the company with the next sync. The sync writes each target with a sync-writer
  session of the pipeline's tenant (`ICrossTenantAccess.OpenSyncWriterSessionAsync`), and the engine lets that
  session write only the copies of that source: never a local entity, never another source's copy, and never an
  insert without its own origin. A dry run no longer reports a forged origin: the engine judges it at the real write.
- **Deletions.** The sync deletes a copy when it gets a delete entity update (`updateKind: DELETE` with the RtId
  of the deleted entity; the copy is deleted with the engine's default strategy, i.e. archived). A deletion in G reaches
  the watch in two shapes:
  - the **default delete** of ApplyChanges and GraphQL **archives** the entity: an update event whose
    `$.Document.RtState` is `1` (Archived);
  - an **erase** removes it: a delete event, which `FromWatchRtEntity@1` delivers only with the opt-in
    `includeDeletes: true` (and `Delete` in `updateTypes`); every existing watch keeps exactly the events it had. It has
    `$.UpdateType` `8`, `$.Document` null and the id at `$.DocumentRtId` (set for every event). The engine matches it on
    the pre-image when the type's collection captures pre-images (`enableChangeStreamPreAndPostImages`; exact for
    derived types sharing the collection, field filters apply to the deleted document), otherwise on the document key
    alone, which is exact only for a watch on the collection root without field filters - every other combination fails
    the trigger start. A typical sync type derives from `System/Entity` and is its own collection root, so no pre-images
    are needed.

  Enums are numbers in the data context. The sync pipeline branches with `If@1` (tested in
  `MasterDataSyncIntegrationTests`):

  ```yaml
  triggers:
    - type: FromWatchRtEntity@1
      ckTypeId: Acme.Crm/Contact
      updateTypes: All
      includeDeletes: true
  transformations:
    - type: If@1                       # erased in G
      path: $.UpdateType
      operator: Equals
      valueType: Integer
      value: 8
      transformations:
        - type: CreateUpdateInfo@1
          targetPath: $._delete
          targetValueKind: Array
          updateKind: DELETE
          ckTypeId: Acme.Crm/Contact
          rtIdPath: $.DocumentRtId
        - type: ApplyChanges@2
          identity: ServiceAccount
          entityUpdatesPath: $._delete
          targetTenants: { relation: Child }
    - type: If@1                       # archived in G (default delete)
      path: $.Document.RtState
      operator: Equals
      valueType: Integer
      value: 1
      transformations:
        - type: CreateUpdateInfo@1
          targetPath: $._delete
          targetValueKind: Array
          updateKind: DELETE
          ckTypeId: Acme.Crm/Contact
          rtIdPath: $.DocumentRtId
        - type: ApplyChanges@2
          identity: ServiceAccount
          entityUpdatesPath: $._delete
          targetTenants: { relation: Child }
    - type: If@1                       # created or changed: upsert the copies
      path: $.UpdateType
      operator: NotEquals
      valueType: Integer
      value: 8
      transformations:
        - type: If@1
          path: $.Document.RtState
          operator: NotEquals
          valueType: Integer
          value: 1
          transformations:
            - type: CreateUpdateInfo@1
              targetPath: $._sync
              targetValueKind: Array
              updateKind: UPDATE
              ckTypeId: Acme.Crm/Contact
              rtIdPath: $.DocumentRtId
              attributeUpdates:
                - attributeName: Name
                  attributeValueType: String
                  valuePath: $.Document.Attributes.Name
            - type: ApplyChanges@2
              identity: ServiceAccount
              entityUpdatesPath: $._sync
              targetTenants: { relation: Child }
  ```
- Not supported with `targetTenants`: association updates, `onDuplicateKey: Report`, record array item updates and
  secret clears (configuration error before any target is opened).
- **Known limitations of the write rule.**
  - Copies change only through the sync, except through CK model or blueprint migration scripts that a tenant admin
    imports; the next sync of a changed source entity overwrites the copy. (The engine maintenance services - CK model
    migrations, blueprint migrations, secret maintenance - get their repository from `IRuntimeRepositoryProvider`
    without the rule, so a child can still upgrade a model that holds copies.)
  - The bulk import (`BulkInsertRtEntitiesAsync`: RT import, blueprint seeds, restore) and the `*ForMigrationAsync`
    maintenance methods are not covered: an export and import of a tenant keeps its copies with their origin.
  - A detached or removed group leaves its copies locked in the child: nobody may change or delete them any more, and
    no sync comes. The admin path is an export and import of the data, or the maintenance APIs; there is no feature to
    release copies yet.
  - Associations are not refused: a local entity may link to a copy.
  - A filter write (GraphQL or REST update/delete by filter) skips copies silently instead of refusing them, and an
    atomic record array item update of a copy writes nothing but reports the usual "no item matched" warning; in both
    cases the copy stays unchanged.

### 4.1c Consolidated read over the child tenants (read nodes with `targetTenants`)

The controller of a group tenant G wants the figures of all companies in one place. A consolidation pipeline runs in
G and reads every company with `GetRtEntitiesByType@1` (or `GetRtEntitiesById@1`) and `targetTenants`; ordinary
transform nodes (`SumAggregation@1`, `GroupBy@1`) build the figures per company and in total from the result.

```yaml
- type: GetRtEntitiesByType@1
  ckTypeId: Accounting/Invoice
  identity: Caller               # the controller; ServiceAccount for a scheduled run
  targetTenants:
    relation: Child              # the only relation of v1
    tenantIds: [companya, companyb]   # optional; without it every registered direct child
    onRefused: Fail              # default; Report returns the allowed companies and lists the refused ones
  targetPath: $.invoices
```

- **Targets.** As for the sync (section 4.1b): without `tenantIds` the registered direct children of G at execution
  time; a detached company is no longer one, a grandchild never is. Each target, listed or not, is opened through the
  guard as a `Child` target (relation, identity proof in that company, generation). `Parent` and `Sibling` are not
  offered for a set: the parent is one tenant (`targetTenant`), and a set of siblings has no use case yet.
- **All targets are opened before the first read.** With `onRefused: Fail` (default) one refusal fails the execution
  with `CROSS_TENANT_FORBIDDEN`, the message names every refused tenant, and no data of any company enters the data
  context. With `onRefused: Report` the refused companies are listed in `RefusedTenants` (tenant id and
  `CROSS_TENANT_FORBIDDEN`, never the reason) and their data is absent; the others are returned. The identity
  `System` (abuse case 5) and an invalid configuration always fail, also with `Report`: they would refuse every
  company, and an empty consolidation must not look like a valid one. `System` is refused before the list of children
  is read, so a tenant without children does not turn it into an empty success. A company that could not be verified
  (`target_unverifiable`: registry, identity service, generation store or repository unavailable) fails the read also
  with `Report`: an outage is not a refusal, and it must not drop companies silently from a consolidation.
- **Each item names its source tenant**: the result's `Items` are the entities of all companies, each with the added
  key `SourceTenantId`; `Tenants` lists the read companies with their `TotalCount`. Paging (`skip`, `take`) applies per
  company.
- **Why a pipeline and not GraphQL.** The asset repository's GraphQL API is bound to the tenant of its route: a request
  to `/tenants/{tenantId}/graphQl` is answered from that tenant's database only. `TenantAuthorizationMiddleware`
  (octo-common-services) accepts only tokens of that same tenant, so an administrator of G gets no business data of a
  company through GraphQL (AB#5060); the only exception is a closed list of administration calls (the tenant dump
  mutation and reads of `runtime.operations`, AB#8986), which a parent tenant's user token may send to a registered
  child and which return no business data. A cross-tenant GraphQL query would have to open that middleware to parent tokens and
  carry per-tenant proofs through the schema and the resolvers, which moves the security boundary of every API
  client. The pipeline keeps the boundary where it is: the read runs in the adapter, through the one guard, with a
  right proven by the identity service in every company, and only the figures the pipeline builds leave it.

### 4.2 Backwards compatibility

- No property = `null` = the unchanged code path (the node keeps calling `etlContext.GetSessionForAsync` and
  `etlContext.TenantRepository`).
- The strict deserializer works in our favour: an adapter that does not know `targetTenant` refuses the pipeline at
  registration, so an old adapter can never run a cross-tenant pipeline against its own tenant.
- `ApplyChanges@1` gets no property; a pipeline that adds one fails registration. Cross-tenant writes exist only
  with `ApplyChanges@2` and the identity `Caller` or `ServiceAccount`.

### 4.3 Relative references and data-driven targets

- The relation is always static configuration; only the tenant id of a `Sibling` or `Child` target may come from data
  (`tenantIdPath`). `Parent` takes no id; a `Parent` with an id, a `Sibling`/`Child` without one, or both `tenantId`
  and `tenantIdPath` is a configuration error.
- A data-driven id is allowed only when the node runs as a **verified caller** (`identity: Caller` with a caller the
  trigger verified); the guard checks that before it reads the path. With `ServiceAccount`, with `System`, or with
  `Caller` but no verified caller (lease queue, schedule, watch, event: the node would fall back to the service
  account, section 3) the access is refused (`data_driven_target_needs_caller`; `System` as
  `system_identity_refused`). Otherwise the data would choose the tenant a service account acts in. Such nodes
  name their target statically (`tenantId`, `targetTenants.tenantIds`).
- For a verified caller a data-driven id is checked like any other: same relation check, same identity proof (of the
  caller's own right in the target), same error.
- Tenant ids are trimmed and compared case-insensitively, as in the identity service.

### 4.4 Deploy-time gate (communication controller) and run-time rule (adapter)

Rules in `PipelineElevationInspector`, applied by `AdapterService.EnsureCrossTenantNodesAllowedAsync` before anything
is stored, and again by `CreatePipelineConfigurationAsync` on every push of a stored definition (Update Configuration,
refresh, registration, lease, deploys: an offending pipeline is left out, set to Error with the refusal message and
reported; the others still go out). The REST read of an adapter (`GET adapter/{id}`) projects the same way but only
leaves the pipeline out: a read writes no state and no event. They hold whoever deploys and also with `ServiceAccountGuard:CheckElevation` off:
they are about the pipeline, not about the deployer.

1. **Identity spellings.** Only an absent identity, `Caller` or `0` (trimmed, any case) is the caller; `ServiceAccount`/`1`
   and `System`/`2` are elevated. Any other spelling the adapter's binder would accept (`01`, `+2`,
   `Caller, ServiceAccount`) is refused at deploy and counts as elevated in every rule.
2. **System.** `identity: System` (or an unrecognised identity) on a node with `targetTenant` or `targetTenants` →
   deploy refused (`FindSystemCrossTenantNodes`).
3. **Service-account and system nodes name a target tenant only statically.** A `ServiceAccount` or `System` node with
   a data-driven target tenant — `targetTenant.tenantIdPath`, any other member of the setting named like a path, or a
   setting that is no mapping — is refused (`FindDataDrivenTargetTenants`, error
   `PipelineElevatedDataDrivenTargetTenant`). A `Caller` node with a `tenantIdPath` deploys with a warning. No path is
   classified: every data-driven target counts, whatever it reads.
4. **Every node is checked.** The rules walk nested nodes too (`Switch@1` `cases[].transformations` and `default`).
5. **Unreadable definition.** A definition the controller cannot read is refused instead of skipping the rules.

At **run time** the adapter's guard (section 6.2) enforces the counterpart: a data-driven target only with a verified
caller, so also a `Caller` node that runs without one (it falls back to the service account) is refused
(`data_driven_target_needs_caller`), and the relation, the identity proof and the system refusal are checked again for
every access.

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
| 15 | Generic-create bypass: T's method-invocation marker used for inserts in U | the marker is never carried to U: the batch is judged without a method run, the insert is refused | — (generic-create guard) |
| 16 | Identity escalation by writing U's identity data | refused cross-tenant, also with a valid grant: MethodOnly identity attributes and identity association edges need a method run, which a cross-tenant write never has | — (attribute write guard) |
| 17 | Cross-tenant association (entity in U associated to an RtId of T) | forbidden: every association end must be inserted by the batch or held by U | `cross_tenant_association` |
| 18 | Second-factor user: caller token without second-factor evidence where U demands it | the identity service refuses → forbidden | `identity_not_provable` |
| 19 | Locked-out or disabled source user | the identity service refuses → forbidden | `identity_not_provable` |
| 20 | Registry, identity service or generation store unreachable, or no token of the pipeline's own service account | forbidden (fail closed), execution failed, also with `onRefused: Report` | `target_unverifiable` |
| 21 | Enumeration: probing which tenant ids exist | "not registered", "outside relation" and "no right" give the same message, which names only the relation the node configured, never the one the target has; the reason code goes to the log only | — |
| 22 | Proof reuse across executions or callers | proofs are cached per execution only | — |
| 23 | Watch trigger in T on U's data | not offered; triggers stay on the own tenant | — |
| 24 | Pool adapter (no own tenant) executes T's pipeline | the relation is computed from T (the registration's tenant), never from the adapter's tenant | — |
| 26 | A company forges an origin: its own pipeline or GraphQL writes `RtSyncOrigin` (claiming an entity for G, or pinning one of its own against its edits) | refused by the engine before anything is written | `SYNC_OWNED_ENTITY` |
| 27 | A company edits, deletes or archives synced master data locally (`ApplyChanges@1`/`@2`, GraphQL, any session) | refused by the engine before anything is written | `SYNC_OWNED_ENTITY` |
| 28 | The sync meets a local entity (or another source's copy) under the source RtId | the whole target is refused, the entity stays unchanged; the other targets are written | `SYNC_OWNED_ENTITY` |
| 29 | The sync's input carries an origin of another tenant | ignored; the node stamps the pipeline's tenant | — |
| 30 | A listed sync target is a grandchild, a detached or a foreign tenant | that target refused, named in `CROSS_TENANT_WRITE_FAILED` | as rows 1, 2, 12 |
| 31 | Consolidation with `onRefused: Fail` and one company without a right | execution failed naming the company; nothing of any company read | `identity_not_provable` (log) |
| 32 | Consolidation with `onRefused: Report` and one company without a right | the company listed in `RefusedTenants`, none of its data returned | `identity_not_provable` (log) |
| 33 | `targetTenants.tenantIds` naming a grandchild, the system tenant, a foreign or a detached tenant, the own tenant | that tenant refused (Fail: execution failed; Report: listed) | as rows 1, 2, 11, 12 |
| 34 | Consolidation with the identity `System`, also with `onRefused: Report` | execution failed | `system_identity_refused` |
| 35 | A company's proof of an earlier generation | that company refused | `tenant_generation_mismatch` |
| 25 | Data chooses the tenant a service account acts in (`tenantIdPath` with `ServiceAccount` or `System`, or with `Caller` but no verified caller) | `ServiceAccount`/`System`: deploy refused (controller, section 4.4); at run time refused for every identity but a verified caller, before the data is read | `data_driven_target_needs_caller` |

Rows 9 and 15–17 belong to the write node and are tested there (`ApplyChangesCrossTenantNodeTests`,
`ApplyChangesCrossTenantIntegrationTests`, `GetRtEntitiesTargetTenantConfigurationTests`); rows 26–30 belong to the
sync (`ApplyChangesSyncNodeTests`, `SyncOriginRulesTests`, `MasterDataSyncIntegrationTests`; rows 26–27 also
engine-mongodb `SyncOriginWriteGuardTests`); rows 31–35 belong to
the consolidated read (`GetRtEntitiesMultiTenantNodeTests`, `ConsolidatedReadIntegrationTests`); row 23 is a
non-feature.

## 6. Implementation

### 6.1 Types (`src/MeshAdapter.Sdk/Services/CrossTenant/`)

| Type | Purpose |
|---|---|
| `ITenantRegistryReader` / `TenantRegistryReader` | reads `ISystemContext.GetAllTenantsAsync` with an admin session into a `TenantRegistrySnapshot` (tenant → parent); cached 30 s, refreshed on a miss at most once per second; throws on an unreadable registry |
| `TenantRelationRules` | pure function: is U in T's relation, and if not, why |
| `ICrossTenantIdentityProvider` / `IdentityServiceCrossTenantIdentityProvider` | proves the identity in U and returns a `CrossTenantProof` (tenant, generation, security context); no token leaves it |
| `ICrossTenantAccessGuard` / `CrossTenantAccessGuard` | the one door: configuration → identity mode (data-driven target only for a verified caller) → relation → proof → generation → U's repository and CK cache → `ICrossTenantAccess` |
| `ICrossTenantAccess` | what a node uses: `TenantId`, `Repository`, `OpenSessionAsync()` (only with the U-proven context; no system session) |
| `CrossTenantAccessDeniedException` | error code `CROSS_TENANT_FORBIDDEN`, reason code (log only), node path |

`IMeshEtlContext.OpenTenantAsync(target, identity, dataContext, nodeContext)` is a default interface member that
refuses, so any other implementer keeps compiling and fails closed; `MeshEtlContext` delegates to the guard with the
per-execution caller, caller token, identity resolver and proof cache.

### 6.2 Guard algorithm

1. Check the configuration (relation and id combination).
2. Identity `System` → refused. A `TenantIdPath` without a verified caller (`ServiceAccount`, or `Caller` without
   one) → refused (`data_driven_target_needs_caller`), before the path is read.
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

- An entity synced from a source tenant carries the System CK record `SyncOrigin` (`RtSyncOrigin`: source tenant,
  source RtId, sync time). Idempotency key = source tenant + source RtId, and the copy keeps the source RtId; a re-run
  produces exactly one copy (section 4.1b).
- The engine refuses every other write that sets `RtSyncOrigin` or changes or deletes an entity carrying it
  (`SyncOriginWriteRules` and its decorator of the write funnel in octo-construction-kit-engine-mongodb); the sync opens
  its target sessions with `ICrossTenantAccess.OpenSyncWriterSessionAsync(<pipeline tenant>)`. The adapter only maps
  the refusal (`SyncOriginRefusal` in `src/MeshAdapter.Sdk/Common/SyncOriginGuard.cs`). Entities without `SyncOrigin`
  are never touched by the sync: `ApplyChangesNode2.PlanSyncAsync` refuses the target when it meets one (so a dry run
  reports it), and the engine refuses it again for the sync-writer session.
- Every target is written in its own transaction; a failed target fails the execution and names the target
  (`CrossTenantWriteFailedException`), the other targets stay written.
- The children come from `ICrossTenantAccessGuard.ListChildTenantsAsync` (the registry snapshot); every one is still
  opened through `OpenAsync`.
- The same `SyncOrigin` contract is reused by writers to other targets (e.g. an external database).

## 7. Tests

- Unit: `CrossTenantAccessGuardTests` (one test per abuse case with a fake registry, proof provider and generation
  reader), `TenantRelationRulesTests`, `IdentityServiceCrossTenantIdentityProviderTests` (scripted token endpoint; an
  exchanged token from another or no issuer is no proof).
- Controller: `PipelineElevationInspectorTests` (the rules of section 4.4 per identity) and
  `DeployPipelineCrossTenantGateTests` (deploy refused or warned, one test per case of section 4.4).
- Integration: `CrossTenantAccessGuardIntegrationTests` with real MongoDB (Testcontainers): G with children A and B,
  an outsider X under another parent, a detached child D and a grandchild; a test node opens the target through the
  guard and reads marker entities.
- Write node: `ApplyChangesCrossTenantNodeTests` (only the target's repository and the proven session are written,
  a refusal writes nothing, also in a dry run; the target's CK model and no method marker judge the batch; association
  ends), and `ApplyChangesCrossTenantIntegrationTests` against the same real tenant tree: a released inter-company
  invoice in A becomes an incoming document in B (invoice data and a reference to A's invoice), update and delete are
  visible in B (as `Caller` and as `ServiceAccount`); without a grant in B create, update and delete are refused and B
  is unchanged; without the setting the node writes its own tenant; grandchild, cousin, detached, system tenant, wrong
  or missing relation, a proof for the source tenant, a proof of an earlier generation, `System` and `ApplyChanges@1`
  with a target are refused.
- Consolidated read: `GetRtEntitiesMultiTenantNodeTests` (every target opened as `Child` before the first read, Fail
  reads nothing, Report lists and skips, `System` (also without any child), configuration errors and
  `target_unverifiable` fail also with Report; the session opened in a target is disposed on every path) and
  `ConsolidatedReadIntegrationTests` on `TenantConsolidationFixture` (G with A: invoices 100 + 50, B: 70, C: 999
  without a right, a grandchild: 500): the consolidation pipeline in G returns A = 150, B = 70, total 220; C refused
  with Fail and with Report; the detached company and the grandchild are no part of it; grandchild, system tenant,
  foreign tenant, other group and the own tenant in `tenantIds`, the identity `System` and a stale generation are
  refused.
- Architecture: `CrossTenantCallSiteArchitectureTests` lists every `ISystemContext.Find*Tenant*` call site; a new one
  fails unless it is added to the allow list with a reason.
