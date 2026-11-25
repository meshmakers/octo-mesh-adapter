# Changelog

Notable changes of the OctoMesh mesh adapter. Newest first; entries name their work item.

## [Unreleased]

### Breaking

- `IServiceAccountTokenService.AcquireServiceAccountIdentityAsync(credentials, cancellationToken)` now requires the
  pipeline's tenant: `AcquireServiceAccountIdentityAsync(credentials, pipelineTenantId, cancellationToken)` (AB#9776,
  security: the roles of a foreign tenant must never be applied to the pipeline's repository; an empty value or
  credentials naming another tenant answer `null`). Migration: pass the tenant of the pipeline the identity is
  resolved for, e.g. `AcquireServiceAccountIdentityAsync(credentials, etlContext.TenantId, ct)`; implementations of
  the interface add the parameter.

### Added

- `AnthropicAiQuery@1` option `keySource: Central` (AB#8691): the node calls the AI service's central-key LLM gateway
  (`Adapter:LlmGatewayUrl`) with the pipeline service account's `ai.llm.invoke` token and sends no API key; tool rounds
  and JSON repair calls go the same way. `AiSpendCapReached` and `AiMeterUnavailable` fail the node with messages that
  name cap, spent amount and UTC reset date, or the fail-closed meter. Missing gateway URL or token, or an inline
  `apiKey`, fail before any request. `keySource: Configured` (default) is unchanged.
- Chart value `llmGatewayUrl` (AB#8691) renders `OCTO_ADAPTER__LLMGATEWAYURL`; empty by default (no variable, the
  central-key path fails closed), a value that is not an absolute http(s) URL fails the render. Set it per adapter as a
  ValueOverride (Path `llmGatewayUrl`). Render tests in `tests/chart/test-chart.sh`.

### Changed

- NuGet packages raised to the newest version within the .NET 10 line (AB#9597): MailKit and MimeKit 5.0.0,
  Microsoft.ML and Microsoft.ML.TimeSeries 6.0.0, AngleSharp 1.10.5, ModelContextProtocol.AspNetCore 2.4.0,
  SSH.NET 2026.0.1, System.Security.Cryptography.Pkcs 10.0.15, and the test tooling (xunit, Testcontainers 4.17.0,
  Microsoft.NET.Test.Sdk, coverlet, FluentAssertions, TimeProvider.Testing 10.11.1). The IMAP requeue tests now
  intercept `IMailFolder.MoveToAsync(IList<UniqueId>, IMoveRequest, ...)` because MailKit 5 made the single-id
  overload an extension method. IronOcr, PdfSharp and QuestPDF stay on their versions (see the PR for the reasons).

### Fixed

- Service-account tokens are accepted only from the adapter's own identity service (AB#9776). The `IssuerUri` and
  `TenantId` of a `ServiceAccountConfiguration` are tenant-editable, and the adapter used to send the service
  account's secret — or, without one, its own client credentials — to whatever issuer was configured, and applied
  the unverified `role` claims of the answer to the pipeline's repository. Now every grant (identity, token,
  delegation) goes to `Adapter:IssuerUri` / `Adapter:AuthorityUrl`; a configured issuer other than empty,
  `{{service.authority}}` or an own name (`Adapter:AdditionalValidIssuers` included) and a `TenantId` other than
  the pipeline's tenant are refused before any request; the issued token's signature is verified against the own
  JWKS (asymmetric algorithms only), its lifetime (`exp` required and not past) and its `iss` and `tenant_id` are
  checked before any claim is used.
  `IServiceAccountTokenService.AcquireServiceAccountIdentityAsync` now takes the pipeline's tenant as a required
  argument. Configurations with an empty or own
  issuer and an empty or own tenant work as before.
- `ApplyChanges@2`: concurrent writers across adapter replicas end in one stored write and a reported conflict
  (AB#9466). A MongoDB WriteConflict (112) is now recognised anywhere in the exception chain (the repository
  wraps it), the transaction is aborted and the write retried up to six times with a short jittered backoff
  (about 0.4 to 0.8 s in total). With `onDuplicateKey: Report` a conflict that outlasts the retries writes the
  flag `true` at `duplicateKeyTargetPath`; without Report it fails the pipeline. Before, the wrapped conflict
  failed the pipeline (a CK method answered `METHOD_HANDLER_FAILED` instead of its declared `CONCURRENT_CHANGE`),
  and exhausted retries ended as a silent success although nothing was written.

### Added

- Consolidated read over the child tenants (AB#9771, Feature AB#8673): `GetRtEntitiesByType@1` and
  `GetRtEntitiesById@1` take the optional `targetTenants` (`relation: Child`, optional `tenantIds`; without the list
  every registered direct child; `onRefused: Fail` (default) or `Report`). Every company is opened through the
  cross-tenant guard before the first read. The output is a multi-tenant result: `Items` (each entity with
  `SourceTenantId`), `TotalCount`, `Tenants` and `RefusedTenants`. `Fail` fails the execution with
  `CROSS_TENANT_FORBIDDEN` naming every refused company and reads nothing; `Report` returns the allowed companies and
  lists the refused ones without their data. The identity `System` and configuration errors always fail. Why a
  pipeline and not GraphQL: docs/cross-tenant-access.md section 4.1c.
- One-way master-data sync into the child tenants (AB#9770, Feature AB#8673): `ApplyChanges@2` takes the optional
  `targetTenants` (`relation: Child`, optional `tenantIds`; without the list every registered direct child). Each
  entity update of the batch becomes, in every target, an idempotent upsert or delete keyed by source tenant and
  source RtId: the copy keeps the source RtId and carries the System record `RtSyncOrigin` (System 2.10.0: source
  tenant, source RtId, sync time), stamped by the node and never taken from the input. An entity of the target
  without that origin, or with another source's origin, is never changed or deleted (the target fails with
  `SYNC_OWNED_ENTITY`). Every target goes through the cross-tenant guard and is written in its own transaction; a
  refused or failed target does not stop the others, and the execution then fails with `CROSS_TENANT_WRITE_FAILED`
  naming every failed target. Outside the sync every writer is refused to set or change `RtSyncOrigin` and to
  update, delete or archive an entity that carries it (`SYNC_OWNED_ENTITY`): the rule is the engine's
  (octo-construction-kit-engine-mongodb `SyncOriginWriteRules`, in the write funnel of every tenant repository, so
  GraphQL, `ApplyChanges@1`/`@2` and system sessions alike); the sync writes with a sync-writer session of the
  pipeline's tenant (`ICrossTenantAccess.OpenSyncWriterSessionAsync`) and the write nodes map the engine's refusal to
  `SyncOriginWriteRefusedException`. Entities without an origin are written as before. Needs System 2.10.0 and
  octo-construction-kit-engine-mongodb with `SyncOriginWriteRules`.
  `FromWatchRtEntity@1` takes the opt-in `includeDeletes` (default `false`: exactly today's events): delete events
  then reach the pipeline with `$.DocumentRtId` (needs octo-construction-kit-engine-mongodb with
  `WatchStreamFilter.IncludeDeleteEvents`, and its normalisation of generated record classes in updates, which the
  update of an existing copy needs), so a deletion in the source removes the copies. A write with a single
  `targetTenant` whose target engine refuses the batch now fails the execution with `CROSS_TENANT_WRITE_FAILED`
  instead of only logging (own-tenant writes unchanged). Node reference: `docs/nodes-load.md`,
  `docs/cross-tenant-access.md` section 4.1b.

- Write into another tenant of the subtree (AB#9769, Feature AB#8673): `ApplyChanges@2` takes the optional
  `targetTenant` (same shape as the read nodes). Every create, update, delete and association change then goes
  through the cross-tenant guard first and is written in one transaction of the target with the session proven there
  (`Caller` or `ServiceAccount`; `System` refused); a refusal fails the execution with `CROSS_TENANT_FORBIDDEN` before
  anything is written, also in a dry run. The write checks judge the batch with the target's CK model and never with
  the run's method marker, so MethodOnly attributes, replaced creates and identity association edges are refused in
  another tenant; an association end the batch does not insert and the target does not hold is refused
  (`cross_tenant_association`). `ApplyChanges@1` has no such setting (the key fails registration). Without the setting
  the node writes its own tenant as before. Node reference: `docs/nodes-load.md`.

- Read another tenant of the subtree (AB#9768, Feature AB#8673): `GetRtEntitiesByType@1`, `GetRtEntitiesById@1` and
  `GetRtEntitiesByWellKnownName@1` take the optional `targetTenant` (`relation: Parent | Sibling | Child`, with
  `tenantId` or `tenantIdPath` for a sibling or child). Every such read goes through the cross-tenant guard and runs on
  the target's repository with the session proven there; Hidden attributes follow the target's CK model; a refusal
  fails the execution with `CROSS_TENANT_FORBIDDEN`. Without the setting the nodes read their own tenant as before.
  A `targetTenant` without a relation is a configuration error (never the parent by default). Node reference:
  `docs/nodes-extract.md`.

- Cross-tenant access guard (AB#9767, design AB#9766, Feature AB#8673): `IMeshEtlContext.OpenTenantAsync` is the
  one door for a node to reach its parent, a sibling or a child tenant (`TenantTargetConfiguration`, relations
  `Parent`, `Sibling`, `Child`). The guard checks the relation on the tenant registry, refuses the system identity
  and the fallback to it, proves the executing identity's right in the target through the identity service (RFC 8693
  exchange for a user caller, and for the pipeline's service account through U's shadow client `xt_{T}_{clientId}`
  of AB#9777; the issued token is verified with the shared AB#9776 verifier: signature, own issuer, lifetime, tenant,
  presenter and subject), checks tenant and generation of the proof and
  only then opens the target's repository; every refusal is `CROSS_TENANT_FORBIDDEN` with one uniform message and the
  reason code in the log. The read nodes use it since AB#9768, `ApplyChanges@2` since AB#9769 (above). An architecture test lists every call
  site that opens a tenant. A target tenant taken from data (`tenantIdPath`) is allowed only for a verified caller:
  with `ServiceAccount`, `System` or `Caller` without a verified caller (fallback to the service account) the guard
  refuses it before reading the path (`data_driven_target_needs_caller`), the run-time counterpart of the
  controller's deploy rule. Design and abuse cases: `docs/cross-tenant-access.md`.

- `DetectStreamDataGaps@1` (AB#9588, PR #129): the missing time ranges `[start, end)` and missing slot counts
  of many series in one windowed stream-data archive over the half-open period `[from, to)`, without a row
  limit (internal batching, per-day COUNT on the storage path of AB#7360, paged row reads). The offset of
  `from`/`to` sets the day boundary; there is no time zone, day start or per-day option.
- `DetectStreamDataGaps@1` validity periods per series (AB#9589): `validityPeriods` (inline list of
  `{rtId, from, to}`) or `validityPeriodsPath` (JSONPath to such objects; a non-empty inline list wins). Each period is
  half-open `[from, to)`, `to` unset is an open end; a series may have several, overlapping or touching ones are
  merged. Only the slots inside `[from, to)` AND inside a series' periods are checked: ranges are cut exactly at
  the validity boundaries (so each range can be re-requested as one valid request, e.g. one EDA CR_REQ_PT), a gap
  across a hole between periods becomes two ranges, and the counts cover only those slots. A series without an
  entry is neither checked nor reported (one info line names how many); a path resolving to nothing warns naming
  the path and reports no series; an empty inline list counts as not set; an entry with `from >= to` fails naming
  the entry. `expectedCount` stays the slot count of `[from, to)`. Stretches outside every period are not read
  where that saves work: series without a period are not queried, the per-day COUNT spans from the first to the
  last bin a batch's periods touch, and the remaining row reads cover only the union of the batch's periods
  (partly filled bins are read whole). Without validity periods the node reads and reports exactly as before.
- Release note for `DetectStreamDataGaps@1` (AB#9590): the node ships in the package
  `Meshmakers.Octo.Sdk.MeshAdapter` and in the mesh adapter image. The EDA adapter (`octo-adapter-eda`, where the
  energy pipelines run) does not get it with the mesh adapter release: it references that package
  (`$(OctoCommVersion)`, set by its pipeline) and has the node only after its own build against the new package
  version and its own CD, which needs an approval per stage. Pipelines that use the node on an EDA adapter wait for
  that rollout; the user documentation is the node page `DetectStreamDataGaps@1` in the OctoMesh documentation.
- Sample method pipeline `docs/examples/slot-booking-method.yaml`: book a slot with a unique index and answer
  the declared method error `CONCURRENT_CHANGE` (AB#9466). Integration tests `SlotBookingConflictTests` run it
  against MongoDB (Testcontainers).

### Compatibility

- AB#9466 changes no System CK model. A unique index belongs in a CK type's first version: adding one to an
  existing type that holds data is a Major change of the CK model (AB#5347).
