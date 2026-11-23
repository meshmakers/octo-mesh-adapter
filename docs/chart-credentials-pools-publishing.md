---
description: The Helm chart: the adapter credential, tenant isolation, pool membership and chart publishing per branch.
applies_to: src/charts/**, tests/chart/**, src/MeshAdapter.Sdk/Leasing/**, src/MeshAdapter.Sdk/Configuration/**
---

# Chart, credentials, pools and publishing

## The adapter's own credential in the chart (AB#5072)

`src/charts/octo-mesh-adapter` carries the three env vars the SDK's
`AdapterAccessTokenService` needs to log the adapter in **before** it connects to
`/{tenantId}/adapterHub`. All three are optional and inert when unset — an unconfigured adapter
acquires no token and connects anonymously, which is what the whole fleet does today.

| Env var | Chart value | Notes |
|---|---|---|
| `OCTO_ADAPTER__ISSUERURI` | `.Values.authUri` | **Same value as `OCTO_ADAPTER__AUTHORITYURL`, by decision.** |
| `OCTO_ADAPTER__CLIENTID` | `.Values.serviceAccountClientId` | Non-secret. Written by the communication controller as a `ValueOverride` at deploy time. |
| `OCTO_ADAPTER__CLIENTSECRET` | `.Values.secrets.serviceAccountClientSecret` via `octo-mesh.secretEnv` | 🔴 Secret-flagged; accepts a plaintext string **or** the `{valueFrom: {secretKeyRef: …}}` map the operator produces from `{release}-octo-secrets`, exactly like `secrets.rabbitmq`. |
| `OCTO_ADAPTER__IRONOCRLICENSEKEY` | `.Values.secrets.ironOcrLicenseKey` via `octo-mesh.secretEnv` | 🔴 Secret-flagged, optional (AB#5449). Consumed only by `PdfOcrExtraction@1`; an adapter without it starts and serves everything else, and only an OCR'd document fails. Same two accepted shapes as the client secret. |

🔴 **`AUTHORITYURL` and `ISSUERURI` are two keys for two directions, fed from one value.**
`AuthorityUrl` (`MeshAdapterConfiguration`, this repo) is **inbound** — the issuer secured
`FromHttpRequest@2` routes accept on tokens presented *to* the adapter. `IssuerUri`
(`AdapterOptions`, octo-communication-sdk) is **outbound** — the identity service the adapter
authenticates *itself* against. Two config keys exist because `AdapterOptions` lives in the SDK and
must also serve adapters with no `MeshAdapterConfiguration` (Loxone, Modbus, Zenon, the simulation
plug). They always name the same identity service, so the chart feeds both from `authUri`; a second
chart value could only ever drift. It must be the **public** issuer address — OIDC discovery runs
against it and the communication controller validates the issuer of the resulting token.

⚠️ **`octo-mesh.secretEnv` fails on an empty value**, which is deliberate for the four mandatory
cluster secrets. The client secret is optional, so its `include` sits behind an `if`; dropping that
guard makes every adapter without credentials fail to render.

**Split-horizon issuers (AB#5232):** `.Values.additionalValidIssuers` (a string list) renders as
`OCTO_ADAPTER__ADDITIONALVALIDISSUERS__<n>` and feeds
`MeshAdapterConfiguration.AdditionalValidIssuers` — extra issuer values accepted on top of
`authUri` in the inbound JWT validation (issuer STRING comparison only; signing keys still come
from `authUri`'s discovery document). Canonical use: the local kind dev cluster, where adapters
reach the host identity service as `https://host.docker.internal:5003` while callers mint tokens
via `https://localhost:5003/`. Normally projected cluster-wide by the communication operator's
context values layer (`OPERATOR__ADDITIONALVALIDISSUERS__<n>`, see octo-communication-operator).

The controller side of the wire (which `ValueOverride` paths are projected, why they are not gated on
`ReceivesClusterSecrets`, and why provisioning had to move before the deploy notification) is
documented in `octo-communication-controller-services/CLAUDE.md` → "Phase 4 — the credentials reach
the adapter pod (AB#5072)".

## Tenant isolation — `AdapterOptions.TenantId` is gone (AB#4924, increment 3)

The SDK deleted `AdapterOptions.TenantId` so that shared adapter leasing cannot read a process-wide
tenant where it needs the tenant of the current work item. Full rationale in
`octo-communication-sdk/CLAUDE.md`; what changed **here** is four call sites, all of which are
genuinely process-level and now say so:

| Site | Why `DedicatedTenantId` is correct |
|---|---|
| `Services/ServiceAccountTokenService.ResolveTenantId` | The AB#5115 fallback for a service-account configuration that names no tenant — the adapter's *own* tenant, not any execution's. |
| `Services/HttpRequests/HttpRequestService.IsCallerAuthorizedAsync` | Inbound routes are published under the adapter's own tenant prefix. |
| `Services/HttpRequests/HttpRequestService.GetUri` | The `/{tenant}` route prefix itself. |
| `Configuration/DependencyInjection/ServiceCollectionExtensions` | `CommunicationServiceClientOptions` — the `DeployDataFlow`/`DeployPipeline` node's client talks to the controller *as this adapter*, on its own tenant route. |

All four are **null on a pool member**, which has no tenant of its own — so a mistaken read there
fails loudly instead of returning another tenant's id. That is the point of the rename.

Anything on the **execution** path must use `IEtlContext.TenantId` inside a node, or
`IAdapterTenantScope` (SDK, `Meshmakers.Octo.Sdk.Common.Services`) in the services around the node
layer. `EtlDataOrchestrator` enters that scope once per execution.

✅ **The DI sweep on this side exists since increment 6** —
`tests/MeshAdapter.Sdk.Tests/Leasing/MeshAdapterSingletonTenantFreedomSweepTests`, over
`AddOctoMeshAdapterPoolMember()` **and** `AddOctoMeshAdapter()`. The SDK's own sweep is structurally
blind to this repo's domain services, and this repo is where the caches are. The rule is inverted on
purpose: a singleton whose surface mentions a tenant is **guilty until somebody has looked at it**,
so adding one fails the build until a human has written down why it is safe.

The one entry worth knowing about is `CrateDbConnectionAccess`: it caches a CrateDB datasource **per
tenant schema**. Keyed, so isolation-safe by construction — but **unbounded across leases**, exactly
the shape `ICkCacheService` had before this increment. Sockets and memory, not leakage; the next
candidate for a lease participant if a member ever leases stream-data tenants at scale.
`CrateDbStreamDataRepositoryFactory` (AB#7009) is cleared too: its only process state is a table-keyed
`RecomputeProcessState`, no credential, no tenant field; `DeleteTenantStateAsync` forgets that state.

The tenant-database ownership singletons of engine-mongodb (AB#8796/AB#8672, cleared in AB#8883):
`TenantDatabaseRegistrationLookup`, `TenantOwnershipReview` and `SystemDatabaseMarkerStore` are stateless
(fresh server snapshot per call, admin paths only). `TenantOwnershipConflictTracker` **does** keep
per-tenant state — the ownership conflicts seen on resolve, keyed by tenant, feeding the health check —
and is cleared on every release: `CkModelCacheLeaseParticipant.LeaveLeaseAsync` calls
`InvalidateTenantResolveImportGuards(tenant)`, which also drops the tenant's conflict
(`CkModelCacheLeaseParticipantTests` pins the call). A new engine singleton that keeps per-tenant
state needs such a release hook, not just an entry in the cleared list.

The tenant generation cache of octo-common-services (AB#8999, cleared in AB#9022): `TenantGenerationReader`
**does** keep per-tenant state — the current generation (creation time of the tenant's lifecycle record) of every
tenant id it was asked for, keyed by tenant, at most 60 s and 1024 tenants — and is cleared on every release by
`TenantGenerationLeaseParticipant`, which calls `ITenantGenerationReader.Invalidate(tenant)` as the last leave step
(`TenantGenerationLeaseParticipantTests`); a request still in flight for the released tenant can refill the entry,
bounded by the TTL. `TenantGenerationCheck` is flagged only for its reader field; its log throttle has no tenant in
its key (transport, client id, outcome, decision) and outlives a lease, which can only suppress a repeated warning
line, never expose data. A new common-services singleton with per-tenant state needs the same: a release
hook here, not just an entry in the cleared list.

## Adapter pool membership (AB#4924, increment 6)

`AddOctoMeshAdapterPoolMember()` (`src/MeshAdapter.Sdk/Leasing/`) makes this adapter a **pool
member**: a process handed one borrowing tenant per lease. 🔴 **Call it before
`AddOctoMeshAdapter()`** — the SDK registers `IAdapterTenantScope` with `TryAddSingleton`, and on a
pool member the winner must be the lease-aware scope. Getting that order wrong fails nowhere; it
simply never enforces the lease.

It registers five `IAdapterLeaseParticipant`s, and **the order is the order they are entered; its
reverse is the order they are left**:

| Participant | Enter | Leave |
|---|---|---|
| `TenantGenerationLeaseParticipant` | — | `ITenantGenerationReader.Invalidate` for the released tenant, as the last step (AB#9022) |
| `BorrowerIdentityLeaseParticipant` | exchanges the lease's credential for a token and publishes it as the process identity | empties `IServiceClientAccessToken` |
| `BorrowerDatabaseLeaseParticipant` | hands the lease's database credential to `LeasedDatabaseCredentialSource` | drops the credential and evicts the released tenant's cached repository clients |
| `CkModelCacheLeaseParticipant` | warms the borrower's CK model | `ICkCacheService.Unload` + `InvalidateTenantResolveImportGuards` (resolve attempt guards and the tenant's ownership conflict, AB#8883) |
| `PipelineRegistryLeaseParticipant` | — | `UnregisterAllPipelinesAsync` |

Identity first, because a member that cannot become the borrower must not touch its data at all; the
registry last, because it is the one holding the borrower's credentials once a pipeline is deployed
(`PipelineRegistration.GlobalConfiguration`, AB#5027) and therefore has to be the **first** thing
dropped.

🔴 **`acr_values=tenant:{borrower}` is not optional, and the result is verified.** Since AB#5077 a
token request without it is issued for the **system** tenant — a 403 if you are lucky and a
cross-tenant read if you are not. `BorrowerIdentityLeaseParticipant` therefore reads the issued
token's own `tenant_id` claim (hence the field added to `JwtPayloadReader`) and **fails the lease**
on absence or mismatch. Asserting that a token exists proves nothing; asserting whose it is does.

🔴 **The concept's "`FindTenantRepositoryAsync` caches per process" is wrong about *which* cache.**
Both the tenant context and the tenant repository are constructed fresh on every call. What is
process-wide is `ICkCacheService`. Somebody hunting for a repository cache will not find one and may
conclude the invariant is already satisfied — it is not.

Tests: `tests/MeshAdapter.Sdk.Tests/Leasing/` (identity + sweep) and
`tests/MeshAdapter.Sdk.IntegrationTests/Leasing/LeasedTenantIsolationTests` — the increment-6 entry
criteria over **two real tenant databases**: the interleave on pipeline output, the poison canary,
randomised interleavings, the post-release state (CK cache unloaded, token holder empty, scope left,
no registration behind), the rendered-log assertion and the identity assertion. 🔴 Its work item
resolves the tenant from `IAdapterTenantScope`, **never** from the lease it was handed — otherwise
every assertion would be a tautology about passing the right argument.

✅ **The chart no longer sets the deprecated `OCTO_ADAPTER__TENANTID`** — it renders
`OCTO_ADAPTER__DEDICATEDTENANTID`, so this adapter stops paying for the shim and stops printing the
deprecation warning on every start. The other seven SDK-based adapter charts (Loxone, plug-simulation,
weclapp, modbus-plug, modbus-socket, finapi, eda) still carry the old key and have to follow before
`ConfigureLegacyAdapterTenantId` is removed.

### The chart can deploy a pool member (AB#4924 §9.4)

`adapterPool.poolTenantId` + `adapterPool.poolRtId` turn the deployment into a pool member: the two
render as `OCTO_ADAPTERPOOL__ADAPTERPOOLTENANTID` / `__ADAPTERPOOLRTID` (the values keys are *not*
the env names — see `_env.tpl`; emitting the short spelling binds nothing, which is AB#5303 item 5),
and the member then gets **neither**
`OCTO_ADAPTER__DEDICATEDTENANTID` **nor** `OCTO_ADAPTER__ADAPTERRTID`. Before this, neither value had
any route into the container, so an `AdapterPool` workload deployed a pod that started, found
`AdapterPoolMemberOptions.IsEnabled` false, logged *"started without a configured pool … Doing
nothing"* and looked perfectly healthy for ever.

🔴 **Both ids or neither.** `IsEnabled` requires both, so half a configuration renders as a *dedicated*
adapter rather than as a broken member — a typo in one variable must not produce the silent pod above.

🔴 **No `memberId` value, deliberately.** `EffectiveMemberId` falls back to the machine name, which is
the pod name in Kubernetes. A configured value would give every replica of the deployment the same
member id, and the controller's registry keys members by it.

The controller writes both paths as `ValueOverride`s when it deploys an `RtAdapterPool`
(`PoolService.AppendAdapterPoolMemberOverrides`).

### 🔴 A member needs NO installation-wide database credential (AB#4924 §9.4)

The member used to have to resolve the borrower through the installation's registry, which runs
`listDatabases` on the **admin** connection and reads the system database — six `octosystem` commands
and hundreds of admin authentications were measured **per lease**, not just at startup. It would
therefore have needed the cluster's admin password to serve one borrowed pipeline, while the chart's
only source for `OCTO_SYSTEM__DATABASEUSERPASSWORD` / `__ADMINUSERPASSWORD` is exactly the
cluster-secret tier that `WorkloadReconciler.AppendClusterSecrets` correctly withholds from an
`AdapterPool`. A member pod was undeployable *and* would have been over-privileged if it had deployed.

Resolved by telling the member where the tenant lives instead of letting it find out.
`LeasedDatabaseCredentialSource` implements **both** runtime seams of the engine, fed from the lease by
`BorrowerDatabaseLeaseParticipant`:

| Seam | Answers | Effect |
|---|---|---|
| `ITenantLocationSource` | which database holds the leased tenant | `SystemContext` skips the admin probe and the registry read entirely |
| `ITenantDatabaseCredentialSource` | which credential opens that database | the borrower's own datasource user, for the length of the lease |

Both answer for the leased tenant / its database and for nothing else, and both are dropped on release.
Either one alone would leave the member needing an installation-wide credential.

The chart follows: a pool member renders **no** `OCTO_SYSTEM__DATABASEUSERPASSWORD` /
`__ADMINUSERPASSWORD` at all. Not cosmetic — `octo-mesh.secretEnv` fails on an empty value, so with the
operator (correctly) withholding the tier the render itself would have failed. Host, system database
name and replica set are still rendered; they are not secrets and a connection needs an address either
way. A *dedicated* adapter still fails to render without the two secrets, verified explicitly.

### E3: what a member gets of the estate-wide secrets — pinned by `tests/chart/test-chart.sh`

Decision E3 (2026-10-07): a pool member gets the **IronOCR licence**, and **never** the database /
admin / CrateDB passwords or the SECRET attribute key ring (`OCTO_SECRETENCRYPTION__*`). The operator
withholds the cluster-secret tier from an `AdapterPool` (its `AppendClusterSecretsTests`); the chart
withholds them again, in the env **and** in the chart-owned `<fullname>-backend` Secret — the Secret
used to pack plaintext DB passwords and key ring values for a member if a values file carried them,
even though no env var referenced them (fixed AB#4924, `secret.yaml` + `hasPlaintextBackendSecret`).

`tests/chart/test-chart.sh` (bash + helm, no cluster) renders the chart with `member-values.yaml`
(operator shape **plus** sentinel values for every forbidden secret) in two shapes and with
`dedicated-values.yaml` as the positive control, and searches the **whole** render — sentinels also in
base64. Run it after every chart change; it is not wired into CI yet (would be a `Bash@3` step before
`helm package` in `azure-pipelines.yml`; the agent already has helm for `HelmDeploy@0`).

⚠️ What a member gets from the engine this way is a **detached** tenant context: it reads and writes
entities but performs none of the model management the registry route performs (see
`octo-construction-kit-engine-mongodb/CLAUDE.md`). Deliberate — those are writes on behalf of the
installation that owns the tenant, and a borrowed process is not it.

## Helm chart publishing (AB#4948)

`src/charts/octo-mesh-adapter` is packaged on every build and published to two
places, plus a third for long-lived branch lines:

| Branch | Channel | Chart version |
|---|---|---|
| `main` | dev bucket (`meshmakers.github.io/helm-chart-build/`) | `0.1.<yyMMDDxxx>` |
| `r*` tag | release (`meshmakers.github.io/charts/`) | `<MAJOR>.<MINOR>.<PATCH>` |
| `test/*` | **same** dev bucket | `0.2.<yyMMDDxxx>-test-0-2-dev` — a SemVer **prerelease** |

The test line has to publish at all because the communication operator resolves
a workload's chart from a repo URL at deploy time — unlike the core services,
whose charts the deploy lane packages from a local checkout, there is no
pipeline in that path.

It shares the dev bucket with main, which is only safe because of the
prerelease tag: a workload with an **empty** `ChartVersion` means "newest in the
repo", and that is exactly what the `System.Communication.MainLatest` blueprint
seeds on every dev/test tenant. A stable `0.2.x` chart would out-sort every
`0.1.x` and move the whole dev/test fleet onto branch charts. Helm ignores
prereleases unless the caller pins one, so an unpinned resolve keeps returning
main's newest stable chart — and that holds even when the prerelease carries the
higher number. A 0.2 instance therefore pins `ChartVersion` explicitly
(runtime-state, so the pin survives a blueprint re-apply).

The version itself is derived centrally by
`ci-templates/derive-chart-version.yml` in `meshmakers/helm-chart-build`; the
prerelease tag is derived from the branch so two test lines can coexist in one
index.
