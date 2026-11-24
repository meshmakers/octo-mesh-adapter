---
description: Platform-only secrets, the generic-create guard, attribute access (Hidden, MethodOnly, ReadOnly) and the failure kind of an execution.
applies_to: src/MeshAdapter.Sdk/Common/{PlatformOnlySecrets,SecretAttributes,GenericCreate*,RtAttributeAccess*}.cs
---

# Secrets and write guards

## Platform-only secrets - no pipeline reveals a SecretDefault

A secret default that an Application declares (`System.Communication/Application.SecretDefaults[].SecretValue`) and
the controller projects into every served tenant (`System.Communication/SecretDefault.Value`) may be used only inside
platform building blocks that return a result and never the value. No tenant pipeline, user or role, tenant
administrators included, may obtain it. It is decided **by CK type in code**, not by ownership (every Secret attribute
has ownership Secret) and not by data permissions (decryption needs none):

- `Common/PlatformOnlySecrets.cs` is the one rule: `IsPlatformOnly(ckTypeId)` (also with the CK cache, for derived
  types), `IsPlatformOnlyCkTypeId(text)` and `IsPlatformOnlyAttribute(ckTypeId, attributeName)` for the `SecretDefaults`
  member of the Application.
- `RevealSecret@1` consults it right after it resolved the type and **before it reads anything**, for `Caller` and
  `ServiceAccount` alike (`System` is refused earlier). The error names the type or attribute, never a value; the target
  path stays unset. `GetPipelineConfigByCkTypeId@1` refuses the type id the same way, before it reads the configuration.
  A record array is not addressable by `RevealSecret@1` anyway (`record arrays are not supported`); the explicit
  refusal makes the `SecretDefaults` case a named rule instead of a side effect.
- `SecretDecryptionCallSiteArchitectureTests` enumerates every `.Unprotect(` / `.RevealOrNull(` call site in `src/`
  against an allow-list (today `RevealSecretNode`, `ServiceAccountTokenService`, `VerifyCaptchaNode`) and fails on a new
  one, and on a stale entry. A new decrypting node is added to the list only together with its use of
  `PlatformOnlySecrets`, and the platform's own consumer, `VerifyCaptcha@1`, is the only reader of a
  SecretDefault, in its own system session, by well-known name, in the executing tenant only.
- The controller keeps the type out of the configuration it ships to adapters (see the `octo-communication-controller-services` repository).

### Generic-create guard — inserts of a type whose method replaces the generic create

A CK method with `replacesGenericCreate` (feature F3.7) is the only way to create
an entity of its type and of every derived type. `MeshAdapter.Sdk/Common/GenericCreateGuard.EnsureAllowedAsync(IMeshEtlContext,
ICkCacheService, IEnumerable<IEntityUpdateInfo<RtEntity>>)` is the shared pre-check for the nodes that insert
(ApplyChanges@1/@2, ImportFromExcel — wired later, see "Node wiring" below; the method is async since the
unloaded-cache case needs a load, `EnsureAllowedAsync`). Rules:

- Only `EntityModOptions.Insert` is checked (Update/Replace/Delete cannot create). The replacing method comes from
  the engine helper `GenericCreateReplacement.TryGetReplacingMethod` (resolves inherited methods) on the tenant CK
  cache; the answer is memoized per type per call, so a batch costs one lookup per distinct type.
- An insert of a flagged type passes only if **all three** hold: `IMeshEtlContext.MethodInvocation != null`,
  `MethodInvocation.MethodId` equals the replacing method's `QualifiedMethodId` (ordinal, case-sensitive) and
  `MethodInvocation.PipelineRtId` equals `PipelineRtEntityId.RtId` of the running pipeline. The marker does not
  travel over `ToPipelineDataEvent`, so a chained pipeline is refused (intended).
- Otherwise the **whole batch** is refused before any write: `GenericCreateReplacedByMethodException` with
  `Code` = `GENERIC_CREATE_REPLACED_BY_METHOD` (`GenericCreateReplacement.ReasonToken`) and `Refusals` (CK type,
  method id, cause `MISSING_METHOD_INVOCATION` / `METHOD_MISMATCH` / `PIPELINE_MISMATCH`); the message is for logs
  only. Call it before the dry-run branch and before the first write.

**Node wiring.** All three nodes take `ICkCacheService` (DI) and call the guard once per batch, before the
first write:
- `ApplyChangesNode` (`ApplyChanges@1`, `ProcessObjectAsync`): on the whole `list` read from `Path`, **before the dry-run
  branch**, so a dry run reports the refusal instead of "would apply".
- `ApplyChangesNode2` (`ApplyChanges@2`): on the `entityUpdates` read from `EntityUpdatesPath` (every insert ends up in
  `resultUpdateInfos`; merging only touches non-inserts), **before the dry-run branch**. A dry run of the method's own
  run passes and still writes nothing.
- `ImportFromExcelNode` (`StoreTreeInDatabase`): on the insert list, before the dry-run branch and before
  `ApplyChangesInRepositoryAsync`, so a dry run reports the refusal instead of "would insert". The exception is
  thrown outside the repository `try/catch`, so it fails the node instead of being logged as a store error.
**Direct writers.** The nodes that bypass `ApplyChanges` and call `TenantRepository.ApplyChangesAsync` themselves
take `ICkCacheService` too and call the guard once per batch before the write:
- `CreateFileSystemItemUpdateNode`: on the single file-system-item insert, before the scoped session is opened.
- `CreateZipArchiveNode` (`PersistFileSystemItemAsync`, `persistAsFileSystemItem`): same.
- `RegisterSelfReportedMeteringPointNode`: in `ApplyAsync`, the node's only write helper (customer, metering point,
  anchor, participation period). The guard runs inside the open transaction; the refusal propagates through the
  `catch { AbortTransactionAsync; throw; }`, so a customer written by an earlier helper call is rolled back.
- `SaveTimeRangeSeriesInArchiveNode`: on the anchor entities (`entityUpdates`), before the write session is opened. Found
  by a grep over the call sites; archive rows themselves are stream data and are not checked.
- **`NotificationDeliveryStateStore` is exempt (by decision).** `NotificationDeliveryState` is an internal System
  type (`System.Notification`); a CK author cannot flag a System type, and a flag on a derived/other type never
  applies to it. Guarding would add a tenant-cache access per notification delivery on a hot path without protecting
  anything. If a System type ever gains `replacesGenericCreate`, revisit this. Do not copy this exemption to a node
  whose type is configurable.
- Complete list of `ApplyChangesAsync` callers in `src/` (grep): ApplyChanges@1/@2, ImportFromExcel, the four
  nodes above, `NotificationDeliveryStateStore`. `SaveStreamDataInArchive` / `SaveTimeRangeStreamDataInArchive` write
  stream rows, `UpdateRtEntityIfNewer` only emits update infos for `ApplyChanges`. A new write node must call the guard
  (developer-guide, "Using the guard in a custom write node").
The exception propagates and fails the pipeline run; nothing is written. Consequences worth knowing:
- **`ImportFromExcel` honours dry runs**: after validation, parsing and the guard it records the dry-run
  intent (`entityCount`, `associationCount`, `wouldInsert` = the RtEntityIds that would be inserted) and writes
  nothing (no session, no `ApplyChangesAsync`). Refusals apply in dry runs as well.
- **A flag added later takes effect only after the CK cache reload** on `CkModelChanged`; a run that started earlier works
  with the cache it loaded.
- **Unloaded tenant cache (fail closed).** `GenericCreateReplacement.TryGetReplacingMethod` answers
  "no replacing method" for a tenant whose cache is not loaded, and the repository reloads the cache on its own when it
  writes, so a guard that just asked would wave the insert through. The cache can be unloaded in the middle of a run
  (`CkModelCacheLeaseParticipant.LeaveLeaseAsync`, `CkModelChanged`). `GenericCreateGuard.EnsureAllowedAsync` therefore
  looks at the batch first (no insert: no cache access at all), and when `!IsTenantLoaded(tenant)` it loads the cache with
  `etlContext.TenantRepository.LoadCacheForTenantAsync`. If the load throws or leaves the tenant unloaded the batch is
  refused with `GenericCreateCheckUnavailableException` (`Code` = `GENERIC_CREATE_CHECK_UNAVAILABLE`, inner exception =
  the load failure). It is deliberately a different error than `GENERIC_CREATE_REPLACED_BY_METHOD`: the insert was not
  judged, nothing was written and the run can simply be repeated (retryable).
Tests: `ApplyChangesReplacedCreateGuardTests`, the guard tests in `FileSystemTransitionTests` (file item, zip) and
`RegisterSelfReportedMeteringPointNodeTests`, `SaveTimeRangeSeriesInArchiveReplacedCreateGuardTests`,
`ImportFromExcelReplacedCreateGuardTests` (unit, per node: no marker, dry run refused, dry run of a plain type writes nothing and reports the counts, matching marker, other pipeline/method,
plain type, Update/Replace unaffected) and `ReplacedGenericCreateIntegrationTests` (real orchestrator + MongoDB + CK
cache, model type `MeshAdapterIntegrationTest/ReplacedAccount` with the static method `Provision-1`
`replacesGenericCreate`). The integration test models the marker by building the `MeshEtlContext` with or without it;
there is no controller/lease/event hub in that harness, so the hand-over from the method invocation to the node is
covered only by the controller and communication-sdk tests.

**Where the marker comes from.** The controller's method invoker sets `ExecutePipelineRequest.MethodInvocation`
(direct path) or `LeaseDto.MethodInvocation` (lease path). Direct path: communication-sdk
`FromExecutePipelineCommandNode` copies it unconditionally to `ExecutePipelineOptions.MethodInvocation`. Lease path:
`LeaseCallerCarryThrough.Apply` copies `lease.MethodInvocation` to the options **independent of `CallerBindingMode`**
(it is not an identity). `MeshContextCreatorService.CreateEtlContext` passes it as a constructor argument of
`MeshEtlContext`, a per-execution side channel like `CallerAccessToken` — 🔴 never put it into `Properties` (shared by
all runs of the pipeline). Absent marker means fail closed.

**Rollout order, no compatibility switch** (any switch could only fail open): core-libs train (octo-sdk +
octo-communication-sdk) -> communication controller with System.Communication 4.12.0 -> mesh adapter. Old controller +
new adapter: no marker, the method's own run is refused (availability only) — ship the adapter right after the
controller. New controller + old adapter: the unknown member is ignored and today's gap remains until the adapter is
upgraded.

### Attribute access in pipelines — Hidden, MethodOnly, ReadOnly

`MeshAdapter.Sdk/Common/RtAttributeAccessGuard` applies the CK v2 attribute `access` to the nodes (a pipeline reads the
repository with system rights, so the GraphQL guards do not reach it). Full description, node list and exceptions:
`docs/developer-guide.md` ("Attribute access in pipelines"). Rules in one place:

- **Hidden**: dropped from every entity an extract node or `FromWatchRtEntity@1` emits (a copy; never the repository's
  instance); a field filter, sort order or query column that reaches it fails the node (`RT_ATTRIBUTE_HIDDEN`, node and
  path, no value); never written, not even in a method run.
- **MethodOnly**: readable; written only when `IMeshEtlContext.MethodInvocation` is set, its method belongs to the
  entity's type and its pipeline is the running one (the `GenericCreateGuard` pattern); otherwise the whole batch is refused
  (`RT_ATTRIBUTE_WRITE_RESTRICTED`).
- **ReadOnly**: readable; a pipeline never writes it (also not on insert). A `Replace` that would clear an attribute the run
  may not write is refused. Presence in an update counts as a write.
- **Identity-owned writes**: the same write call enforces the engine's `IdentityOwnedWrites` list (identity-owned
  `System.Identity/User` attributes, incl. `LockoutEnd`/`AccessFailedCount`, and the association roles `AssignedRole`,
  `GroupMember`, `ChildGroup`, `MayActAs`, `GrantsPermission`): refused with cause `IDENTITY_OWNED` (attributes on insert/update/
  replace, associations on insert **and** delete in both directions; messages name type, attribute or role and target type,
  never values or rtIds). Allowed only in the method run of the User type (attributes) or of a `System.Identity` type at either
  end (associations). Write nodes with associations call the overload
  `EnsureWritesAllowedAsync(etlContext, cache, entityUpdates, associationUpdates)` (ApplyChanges@2, SaveTimeRangeSeriesInArchive,
  ImportFromExcel, CreateFileSystemUpdate, CreateZipArchive, RegisterSelfReportedMeteringPoint); a new write node that writes
  associations must pass them. Tests: `Nodes/Access/IdentityOwnedWriteGuardTests`, `IdentityCredentialGuardPipelineTests`.
- **Stored queries**: a persisted query whose own columns, filters, sort orders, attribute-search, group-by or
  aggregation paths reach Hidden is refused before it runs (`EnsureStoredQueryNotHidden`, `GetQueryById@1` for every query kind,
  `PrepareNotification@1`); paths are checked against the query type and its derived types (unknown type: every type). Entity-selector
  keys (`[passwordHash=X]`) count as Hidden paths; error messages remove selector values (`WithoutSelectorValues`: from the first
  selector `=` to the last `]` followed by a valid path suffix with balanced quotes before it (even counts, none open in reading order), else to the end — also unclosed
  selectors and open quoted values; tests `SelectorValueRedactionTests`).
- Write nodes call `RtAttributeAccessGuard.EnsureWritesAllowedAsync` (it runs `GenericCreateGuard` first); extract nodes
  call `DropHidden(Async)` and `EnsureNotHidden`; nodes that run a stored query call `EnsureStoredQueryNotHidden`. Unknown types are not judged; an unloaded CK cache is loaded, else fail closed.
  `SessionNodeTestBase.CkCache` gives tests a loaded cache that knows no type.
- **Credential guard regression tests**: `tests/MeshAdapter.Sdk.IntegrationTests/Nodes/Access/IdentityCredentialGuardPipelineTests`
  run the real orchestrator and nodes against the **real** System.Identity model (compiler output in `TestData/ck-models/`, loaded by
  `Fixtures/SystemIdentitySnapshotCkModel`; refresh procedure in the README there) with a user whose `PasswordHash`, `SecurityStamp` and
  `UserTokens` hold fixed dummy values (`IdentityCredentialPipelineFixture`): extract nodes (`GetRtEntitiesByType@1`, `GetRtEntitiesById@1`,
  `GetAssociationTargets@1`, `GetQueryById@1` with a stored Hidden column) emit no credential, filters and sorts on them are refused,
  `ApplyChanges@1/@2` refuse Hidden, MethodOnly and ReadOnly user attributes (Hidden also in an identity method run); no output, error or
  captured log line contains a dummy value; a stored query whose own filter reaches a credential is refused by `GetQueryById@1`
  (`GetQueryById_WithACredentialFilterInTheStoredQuery_IsRefused`).

### Failure kind of a failed execution — unacknowledged writes

A pipeline method whose ApplyChanges write was not acknowledged (MongoDB write-concern error under `w:majority`, or
`UnknownTransactionCommitResult`) may have written; the caller must get `METHOD_OUTCOME_UNKNOWN`, not
`METHOD_HANDLER_FAILED` (rule R3, same as service handlers). The adapter is the only place that still sees the
exception, so it classifies it with `PipelineExecutionFailureKinds.Of(ex)` (octo-sdk, delegating to the engine's
`UnacknowledgedWriteClassifier`) and reports the structured kind next to `Failed`, at three places:
`MeshAdapterTriggerContext.EndExecutePipelineAsync` (pipeline task faulted), `ReportExecutionFailedBeforeRunAsync` (context
creation failed) and `LeasedPipelineWorkItem` (`LeaseWorkOutcome.Failed(message, kind)`; an exception that escapes the work
item is classified by `AdapterPoolClient`). The controller stores it on `PipelineExecution.FailureKind` (System.Communication
4.14.0) and maps it. Never a marker in the error message. Tests: `MeshAdapterTriggerContextTests` (`*NotAcknowledged*`,
`*UnacknowledgedWrite*`), `Leasing/LeasedPipelineWorkItemFailureKindTests`.
