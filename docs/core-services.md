---
description: The adapter core services: startup, ETL context, HTTP routing, service account tokens and event writing.
applies_to: src/MeshAdapter.Sdk/Services/**, src/MeshAdapter/**
---

# Core services

### Core Services

- **MeshAdapterService**: Main service handling adapter startup/shutdown and pipeline registration.
  Also implements `IAdapterService.CkModelChangedAsync`: when the communication controller
  broadcasts a CK model change (after `ImportCk` / `ClearCache`), the tenant's CK cache is unloaded
  and lazily reloaded on the next pipeline execution — without this, the load-once CK cache
  (`ModelLoaderService` guard) would keep validating pipeline writes (`CreateUpdateInfo@1` /
  `ApplyChanges@2`) against the old model until the process restarts.
- **MeshEtlContext**: ETL context implementation providing access to repositories and pipeline state
- **HttpRequestService**: Handles dynamic HTTP routing and request processing
  - **HttpResponseGuard**: `SendRequestAsync` runs every route response through `HttpResponseGuard` before it is written. It removes the reserved request keys (`path`, `method`, `principal`, `headers`, `body`, `bodyEncoding`, `query`, `files`, `formData`, `contentType`, `contentLength`) from the top level of an object response and any whole copy of this request's `headers`/`principal` object anywhere (also in ForEach arrays); a single deliberately mapped field stays. Covers `FromHttpRequest@1/@2` and `FromTeamsBot@1`. 🔴 No switch re-enables the echo (decided by Gerald 2026-10-14); a removal is logged once per route and key set, key names only. Tests: `HttpRequestServiceTests` (region *Response guard*) and `FromHttpRequestNode2AuthorizationTests.UnshapedPipeline_…`.
- **MeshContextCreatorService**: Creates contexts for pipeline execution
- **MeshAdapterTriggerContext**: The mesh adapter's `ITriggerContext` — starts and ends one pipeline execution per trigger event. A hand-maintained copy of the SDK's `internal` `AdapterTriggerContext` (not subclassable from here), kept because the ETL context type argument is `IMeshEtlContext`, with deliberate deltas the SDK lacks (and the same `ReportStatusAsync` override the SDK host has — the trigger's status line goes through `IPipelineExecutionReporter.ReportPipelineStatusAsync` beside the execution reports; keep the two copies in step): when the registration vanished while the adapter reconfigures, `Start` logs a warning instead of an error but still throws `PipelineNotFound`, `End` returns null instead of throwing, and a failed execution is logged before the rethrow. **Dry run:** `ExecutePipelineOptions.IsDryRun` — set by `FromExecutePipelineCommand@1` from an `ExecutePipelineRequest` — only reaches the nodes if the host turns it into a `DefaultPipelineExecutionMode { IsDryRun = true }` on the orchestrator call and, when the pipeline has debugging off, forces the registered `IPipelineDebugger` on so the recorded intents have somewhere to go (that also captures every node's input/output snapshot, as any debug-enabled run does). The SDK host gained that threading in r3.4.18; this host was never updated, so the dry-run-honouring Load nodes (`DryRunHonouredLoadNodes`) saw a null mode and ran for real. Only the top-level orchestrator call carries the mode: pipelines started by `ToPipelineDataEvent@1`/`FromPipelineDataEvent@1` and the `BufferData@1` flush sub-pipeline do not (SDK). When the SDK host changes how it hands an execution to the orchestrator, mirror that handoff here and keep the deltas above — nothing in the type system enforces the parity; `MeshAdapterTriggerContextTests` pins the orchestrator contract and `MeshAdapterTriggerContextDryRunTests` proves the mode reaches a real node. **The start is reported BEFORE the context exists:** `StartExecutePipelineAsync` generates the id, reports `ReportExecutionStartAsync`, and only then calls `CreateEtlContext` inside a try/catch that logs at Error, reports `ReportExecutionEndAsync(Failed, ex.Message)` under the same id and rethrows (`ReportExecutionFailedBeforeRunAsync`). The other order is how the finAPI adapter in production failed on every cron tick for four days (`MeshContextCreatorService` → `FindTenantRepositoryAsync`: "System tenant database does not exist") without the controller ever recording an execution — `PipelineStatistics` frozen, `octo.pipeline.execution.failures` at zero, `OctoMeshPipelineExecutionFailing` silent. No double report: `RegisterExecution` runs only after the try, so `End` never sees a failed id; same order as the SDK host, keep them in step. Tests: `StartExecutePipelineAsync_WhenContextCreationFails_*`, `StartExecutePipelineAsync_ReportsTheStartBeforeTheContextIsCreated`.
- **ServiceAccountTokenService**: Acquires OAuth2 tokens from `ServiceAccountConfiguration` entities for service-to-service REST calls (used by `DeployPipelineNode`, `AnthropicAiQueryNode`, `McpToolCallNode`).
  🔴 **It never writes the process-wide `IServiceClientAccessToken`** — that instance *is* the
  adapter's own service identity (it doubles as `ICommunicationServiceClientAccessToken` towards the
  communication controller; `AdapterAccessTokenService` in octo-communication-sdk keeps it current and the
  lease participant swaps it for the borrower's). The service does not even take it as a dependency
  (test `TheServiceCannotReachTheAdaptersProcessWideTokenHolder`). The former `EnsureTokenAsync` wrote the
  service account's token there behind ONE expiry field keyed by nothing, so a second account got the
  first one's token, the adapter's refresh replaced the account's token (and the account's replaced the
  adapter's), and a configuration with a foreign `TenantId` put a foreign-tenant token into the adapter
  identity. `AcquireServiceAccountTokenAsync` runs the **client-credentials** grant (or the
  impersonation grant) and **returns** the token, cached per `(TenantId, ClientId, IssuerUri)`.
  `AcquireDelegatedTokenAsync` runs the OctoMesh **delegation grant**
  (`grant_type=urn:meshmakers:params:oauth:grant-type:on-behalf-of`, implemented in
  `octo-identity-services`): the service account authenticates with its own credentials and presents
  the end user's token as `subject_token`, so the issued token runs on the **user's** `sub` with the
  intersection of both parties' roles. 🔴 It **returns** that token instead of storing it — writing a
  user-bound token into the process-wide singleton would leak one caller's identity into every
  concurrent request. Same-tenant only, so the configuration's `TenantId` is required
  (`acr_values=tenant:X`); `offline_access` is never requested (the identity service rejects it —
  a refresh token would freeze the role intersection). Delegated tokens are deliberately **not
  cached**: any cache would have to be keyed by subject, and the only key material is the caller's
  own token.
- **AdapterEventService**: Writes `System.Notification/Event` entries tagged with the `MeshAdapter` source into the tenant's event log, the audit trail Studio shows under Repository → Events. Used to record authorization decisions on secured trigger routes and the notification engine's warnings; failures degrade to a log warning so auditing can never fail a request
- **Notification events**: a failed notification run fails the pipeline, and the communication controller records that as an Error event, so the engine writes no error of its own (it would be a duplicate). What the controller cannot see because the run still succeeds goes to Events as a Warning linked to the subscription (the preference for a taken-over lease): rows that could not be read for the mail, a failure that could not be recorded on the recipient, a run whose lease another run took over, an unknown time zone. The recipient is named by e-mail. The unknown time zone is reported once per adapter process and preference, and again only when the value changes, since it would otherwise repeat on every run.
