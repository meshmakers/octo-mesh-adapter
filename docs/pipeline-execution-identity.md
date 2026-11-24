---
description: How every pipeline session is classified and resolved (caller, service account, system) and the test guard that enforces it.
applies_to: src/MeshAdapter.Sdk/Services/PipelineIdentityResolver.cs, src/MeshAdapter.Sdk/Services/MeshEtlContext.cs, src/MeshAdapter.Sdk/IMeshEtlContext.cs
---

# Pipeline execution identity

## Pipeline execution identity — every session is classified

Pipeline execution runs under a real identity instead of anonymous, parameterless system sessions.
The mechanism is a single resolution point, two methods on the context, and a classification that is
written down at every call site.

### Resolution — once per execution, lazily

`MeshContextCreatorService.CreateEtlContext` is the one point every execution flows through, so it
builds one `PipelineIdentityResolver` per execution and hands it to `MeshEtlContext`. Precedence:

1. **`ExecutePipelineOptions.VerifiedPrincipal`** → `RtSecurityContext.ForUser(sub, roles)`.
   Free: it is already on the options.
2. **The adapter's / pipeline's service account**. The communication controller projects
   the `ServiceAccountConfiguration` into the pipeline's configuration list, so the credentials come
   out of `IGlobalConfiguration.GetAllRawJsonByCkTypeId("System.Communication/ServiceAccountConfiguration")`
   with no repository read. The **roles** are the expensive half — they are not on the entity, only
   as `role` claims on the issued token — so `IServiceAccountTokenService.AcquireServiceAccountIdentityAsync`
   requests a client-credentials token, parses it locally (`JwtPayloadReader`, no signature check:
   the token is the answer to our own request over TLS and never passed through a caller) and caches
   the result per `(TenantId, ClientId)` until shortly before the token's own `exp`.
3. Otherwise `RtSecurityContext.System`.

Resolution is **lazy and memoised**: many executions never open a session at all (high-frequency
event triggers), and they must not pay a token round trip. The identity cache never holds a credential,
so it is separate from the token cache of `AcquireServiceAccountTokenAsync`.

🔴 **Fail-closed once an account is configured.** A failed acquisition throws
(`MeshAdapterPipelineExecutionException.ServiceAccountIdentityUnavailable`) instead of falling back to
the system context. The system context bypasses data-level permissions entirely, so a
fallback would fail *open*: an identity-service outage would silently widen every read and leave every
write unstamped, indistinguishable from a correctly restricted run. The System path survives only
where **nothing** is configured — the earlier fleet and every tenant until provisioning has run —
because changing behaviour there would take the whole fleet down.

⚠️ **Two caveats.** `GetAllRawJsonByCkTypeId` matches `ConfigurationTypeId.SemanticVersionedFullName`,
which appends `-N` as soon as the CK **type** version passes 1: a type bump makes the match go quiet
and every pipeline fall back to the system context, and that failure looks like "nothing happened",
not like an error — a bump has to be paired with a change to
`PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId`. And the adapter caches
`GlobalConfiguration` at pipeline **registration**, so changing the linked service account only takes
effect after the pipeline / data flow is redeployed.

### Distribution — `IMeshEtlContext`

| Method | Meaning |
|---|---|
| `GetScopedSessionAsync()` / `GetScopedSession()` | The effective identity. Stamps `RtCreatedBy`, subject to data permissions. |
| `GetSystemSessionAsync()` / `GetSystemSession()` | **Explicitly** `RtSecurityContext.System`. |

The second is the more important one. Its existence is what turns "which identity does this node use"
from an accident of who last touched the call site into a decision written down in the code: a node
either says scoped or it says system, and a new node has to choose. **No node calls
`TenantRepository.GetSessionAsync()` any more** — `TenantRepositorySecurityExtensions` degrades into
that overload *silently* for a repository without `ISecureSessionFactory`, which is exactly the trap
this closes. The only remaining parameterless system session in the SDK is
`ServiceAccountTokenService.ReadConfigurationAsync`, which is circular by nature: it is the read that
*answers* the identity question.

### Classification (45 call sites: 25 scoped, 20 system)

**System by decision** — each carries a code comment saying what breaks if it were scoped:

| Node | Why system |
|---|---|
| `ImportFromExcel@1` (sync) + its `WellKnownNameLoader` (sync) | An import is a bulk load belonging to the tenant; a creator stamp makes every imported row invisible to an OwnedOnly reader, and a filtered name lookup re-creates existing entities as duplicates. |
| `ImportDataPointMappings@1` / `ExportDataPointMappings@1` | Backup/restore pair. A read filter writes a *shorter* export file that looks complete, and the loss only surfaces on restore. |
| `DeployPipeline@1` | Reads pure platform types and calls the controller as the adapter's service identity — a service-identity node by construction. |
| `GetNotificationTemplate@1` | Platform configuration; a filter turns "may not see" into the same hard `TemplateNotFound` as "does not exist". |
| `CheckDuplicate@1` | Must see other people's documents, or it reports "no duplicate" and the record is created twice — silently, in exactly the case the node exists to prevent. |
| `BackfillFromRtEntity@1` | Would not find the entity and backfill nothing, with a green execution. |
| `SaveTimeRangeStreamDataInArchive@1` | The read is an orphan guard; a filter turns it into a hard "refusing to insert". |
| `GetFileSystemContent@1`, `SendEMail@1`, `SftpUpload@1`, `ToDiscord@1` (×2) | Binary download for outgoing channels: attachments regularly belong to somebody else, and a filter does not fail the node — the message goes out without its attachment. |
| `ApplyChanges@1` | Frozen: the deprecated twin of `@2`. A pipeline still on `@1` must not start stamping or filtering because the adapter was upgraded. Migrate to `@2` to get an identity. |
| `CreateZipArchive@1` / `CreateFileSystemUpdate@1` — their `GetFolderRootAsync` helpers | The `FolderRoot` (`System.Files` first, `System.Reporting` fallback) is platform configuration; a filtered root reads as missing and the artefact is never written at all. |
| `PrepareNotification@1` (configuration read) and `NotificationDeliveryStateStore` — the writes `CommitNotificationState@1` goes through and the re-read that decides who holds the lease | Preference, subscription, template, query definition and delivery state are notification configuration and engine bookkeeping, not the recipient's data. A hidden subscription would read as removed and stop the mails without a trace; a hidden state reads as missing and the next run mails everything again. |

**File nodes: `System.Files` first, `System.Reporting` fallback (concept §8.6).** `CreateFileSystemUpdate@1`, `CreateZipArchive@1` (persist mode), `GetFileSystemContent@1` and `ToDiscord@1` resolve through `Nodes/FileSystemCkTypes`. A folder root is looked up by well-known name as `System.Files/FolderRoot` first, then `System.Reporting/FolderRoot`; the new item gets the type family of the root that was found — so an unmigrated tenant (even with `System.Files` imported and an empty `Files` root) keeps writing `System.Reporting` items exactly as before. Items read by RtId are searched as `System.Files/FileSystemItem`, then `System.Reporting/FileSystemItem`. A lookup against a type the tenant does not know counts as "not found there" (only when no type answers at all is the repository error surfaced). The helpers receive the caller's session and open none themselves, so the session classification is unchanged. Remove the fallback when System.Reporting 3.0.0 (file types dropped) is installed everywhere. Tests: `tests/MeshAdapter.Sdk.Tests/Nodes/FileSystemTransitionTests.cs`, `Nodes/Load/ToDiscordNodeTests.cs`.

**Two identities in one node:** `PrepareNotification@1` reads its configuration and state as system but runs the subscription's query scoped, under the pipeline's identity; `CreateZipArchive@1` and `CreateFileSystemUpdate@1` write real user
artefacts (scoped, stamped) but resolve their FolderRoot as system. Keep the split.

**Everything else is scoped**, including `ApplyChanges@2` (its caller-scoped branch no longer falls back to
a system session when there is no verified caller — the fallback is now the service account) and
`UpdateRtEntityIfNewer@1`. Three of them carry a warning in the comment: `ValidateDataPointCoverage@1`
produces **false positives** ("coverage missing") under a narrow identity,
`GenerateDataPointMappings@1` can propose duplicates of mappings it cannot see, and
`SaveTimeRangeSeriesInArchive@1` inserts a **duplicate anchor** rather than failing when a narrow
identity hides an existing one. That is the right trade there — it reads *and writes* the tenant's own
measurement entities, so it must be stamped and subject to data permissions like any other
business-data node; the alternative (System) would write unstamped entities nobody can scope
afterwards. The no-orphan invariant holds either way, since the duplicate is a real written entity.

**Not touched:** the session-less CrateDB / stream-data paths (`SaveStreamDataInArchive@1`,
`GetStreamData@1`, `AggregateStreamData@1` — they go through `IStreamDataRepository`, which opens its
own sessions internally) and `AdapterEventService`. No session is in play there.

### The test guard

`SessionNodeTestBase` (unit tests) is the reason this stays true. It fakes the repository as
`A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>())` — without that face the
security-context extension falls back to the parameterless system session **in silence**, which is
why the caller-scoped branch added to `ApplyChanges@2` was green for months without ever
enforcing anything. Two guards are armed: the parameterless overloads throw, and a system session
throws until a test declares `GivenSystemSessionIsExpected()`. After every test, any caller-scoped
session is checked to have carried the full identity (subject *and* roles) — a `ForUser(null, [])`
context is not the system context and would otherwise sail through.

`PipelineIdentityMatrixTests` is its sibling for the entry points — see the delegation matrix section below.

`SessionIdentityClassificationTests` scans `src/MeshAdapter.Sdk/Nodes` (located via
`[CallerFilePath]`) and pins the table above file by file, that no node reaches the repository
directly, that every call site carries its classification reasoning, and that exactly the two known
synchronous sites are synchronous. `SessionIdentityBehaviourTests` drives the nodes that had no suite
of their own. `SessionIdentityIntegrationTests` verifies the same contract against the **real**
`TenantRepository`, where `session.GetSecurityContext()` is the truth.

### The identity ends at a pipeline chain — by decision, and visibly

`ToPipelineDataEvent@1` → `FromPipelineDataEvent@1` (both in `octo-communication-sdk`) crosses the
message bus, and the trigger on the far side builds its `ExecutePipelineOptions` **without** a
`VerifiedPrincipal` and **without** a caller token. So an HTTP-triggered pipeline that chains to a
second one runs the first half as the user and the second half as the service account.

🔴 **That is the decision, not a gap.** Forwarding the identity would let a pipeline act as a caller
the *target* never authenticated: the sender picks the routing key, so whoever may enqueue into the
data flow would inherit whoever last triggered the sending pipeline — and on the fire-and-forget path
the message has no bounded lifetime, so the identity would stay usable for as long as it sits in the
queue. A privilege escalation is not something to introduce as a side effect of a chaining node. If a
chained execution should ever run as the user, the identity has to be **established** on the far side
(verified), never relayed.

What the decision costs is that one logical request runs under two identities, so the transition is
made visible instead of silent: `ToPipelineDataEvent@1` records the hand-off on the **execution log**
(`INodeContext.Info`, the channel the adapter and the Studio debug panel already surface) naming the
subject whose identity ends there and the target pipeline that will resolve its own. Deliberately not
on the message — its payload is pipeline data, and no credential may travel on it — and deliberately
not a new audit channel. Without a caller identity the same site logs at debug level: the overwhelming
majority of chains are service-to-service and an info line for each would drown the case that matters.

`FromPipelineDataEventNodeTests` and `FromExecutePipelineCommandNodeTests` (in the SDK repo) pin that
the second execution really starts with neither value, so a well-meant "the identity should survive
the chain" change fails a test rather than shipping.

### The delegation matrix — one place where the rules are proven together

`PipelineIdentityMatrixTests` joins what the individual suites cover into the statement the platform
actually makes, across every trigger kind (HTTP with a verified caller, HTTP anonymous,
cron/`FromPipelineTriggerEvent@1`, `FromPipelineDataEvent@1`, `FromExecutePipelineCommand@1`, and the
channel triggers) and every identity situation:

| Rule | Where it is pinned |
|---|---|
| Precedence: verified caller ▶ service account ▶ system | `PipelineIdentityMatrixTests` (per trigger kind), `PipelineIdentityResolverTests` |
| Intersection is over **role names**; the **subject is the caller**, so owner-scoped checks (`RtCreatedBy`, `ownerAttributePath`) are about the human | `ServiceAccountTokenServiceTests.AcquireDelegatedTokenAsync_TheSubjectStaysTheCaller…`, `SessionIdentityIntegrationTests.WithBothACallerAndAServiceAccount_TheSessionActsAsTheCaller` |
| 🔴 An **empty intersection is fail-closed and identity-side a SUCCESS** — a valid token that simply carries no roles, whose only symptom is that nothing comes back | `ServiceAccountTokenServiceTests.AcquireDelegatedTokenAsync_AnEmptyRoleIntersectionIsASuccess…`, `PipelineIdentityMatrixTests.AnEmptyRoleIntersectionResolvesQuietlyToAnIdentityWithNoRoles` |
| A caller **with** an identity but **no roles** sees nothing on a protected type — `ForUser(sub, [])` is not the system context | `SessionIdentityIntegrationTests.ACallerWithoutRolesProducesANonSystemSessionWithNoRoles` |
| **No service account configured** ⇒ the System path, unchanged (the fleet before provisioning) | `PipelineIdentityMatrixTests.WithoutAServiceAccount_ATriggerWithoutACallerKeepsTheSystemPath` |
| **Configured account whose token cannot be had** ⇒ abort, never a System fallback | `PipelineIdentityMatrixTests`, `SessionIdentityIntegrationTests.AConfiguredServiceAccountWhoseTokenIsUnavailableOpensNoSessionAtAll` |

Two of these deserve their own warning. The **empty intersection** looks like a bug from the outside —
the assistant answers "I found nothing" — and the obvious repair is to treat a role-less delegated
token as a failed acquisition. That must never happen: `AnthropicAiQuery@1` turns a null token into a
hard failure, so rejecting a role-less one would turn a correctly restricted answer into an outage,
and the pressure to relax it back towards the service account's own reach is exactly how a delegation
feature loses its point. And **no caller must ever fall back to the service account because the caller
has no roles** — that would hand a role-less user the account's full reach.

`PipelineIdentityMatrixTests` also scans `src/MeshAdapter.Sdk/Nodes/Trigger` and pins that
**`FromHttpRequestNode2.cs` is the only trigger that sets `VerifiedPrincipal` / `CallerAccessToken`**,
plus the list of triggers that start an execution at all — the same house pattern
`SessionIdentityClassificationTests` uses for the session call sites. A trigger that starts forwarding
a principal changes the identity every pipeline behind it runs as, and does so invisibly: nothing
fails, the execution just sees different data.
