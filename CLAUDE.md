# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

> **Important**: This file must be kept up-to-date when the codebase changes. When adding new nodes, services, or modifying the architecture, update the relevant sections accordingly. Also update `docs/developer-guide.md` for comprehensive changes.

## Project Overview

This is the Octo Mesh Adapter project - an adapter that manages and executes mesh pipelines. It's a .NET 10.0 solution consisting of three main projects:

- **MeshAdapter**: The main executable service
- **MeshAdapter.Sdk**: SDK containing pipeline nodes and services
- **MeshNodes.Sdk**: Node configuration definitions

## Build Commands

```bash
# Build the solution
dotnet build

# Build in Release mode
dotnet build -c Release

# Build in DebugL mode (uses local NuGet packages from ../nuget)
dotnet build -c DebugL

# Run the main adapter
dotnet run --project src/MeshAdapter/MeshAdapter.csproj

# Clean build artifacts
dotnet clean
```

## Architecture Overview

### Pipeline Node System

The adapter implements an ETL (Extract-Transform-Load) pipeline system with nodes organized into four categories:

1. **Extract Nodes** (`src/MeshAdapter.Sdk/Nodes/Extract/`): Data retrieval nodes
   - GetRtEntitiesByIdNode
   - GetRtEntitiesByTypeNode
   - **GetQueryByIdNode** — Executes a persisted query by RtId. Resolves the shared `RtPersistentQuery` base, so the caller does not need to know the kind in advance. Supports runtime-data queries (`RtSimpleRtQuery`, `RtAggregationRtQuery`, `RtGroupingAggregationRtQuery`) via `TenantRepository.GetRtEntitiesGraphByTypeAsync`, and **stream-data queries** — simple (`RtSimpleSdQuery`), aggregated (`RtAggregationSdQuery`), grouped-aggregated (`RtGroupingAggregationSdQuery`), and downsampled (`RtDownsamplingSdQuery`) — via the tenant's `IStreamDataRepository` (resolved through `ISystemContext.FindTenantContextAsync(tenantId).GetStreamDataRepository()`, same pattern as `SaveStreamDataInArchive`). Stream-data result shapes mapped into `QueryResult`: simple → **time series** with a leading `Timestamp` column then the projected attribute columns (differs from the runtime simple query, which is one row per entity with no timestamp); aggregated → single row; grouped → one row per group (group-by columns then aggregates); downsampled → **binned time series** with a leading `Timestamp` (bin start) then one column per aggregation, one row per bin (empty bins keep their timestamp and carry null aggregates). Projected values are looked up by their physical CrateDB column name — attribute path with dots stripped and lower-cased (`amount.value` → `amountvalue`); aggregate values by `{physicalColumn}_{funcToken}` (`amountvalue_sum`). Optional `From`/`To`/`Limit` config values override the time range / row cap persisted on the query, and `FromPath`/`ToPath`/`LimitPath` read those same three values from the pipeline data via JSONPath (for ranges computed upstream, e.g. an HTTP trigger) — precedence per value is literal → path → persisted. Path values may be ISO-8601 strings or date/time values; a path value without a time-zone offset is read as UTC, so the adapter's local zone never shifts the window (a *literal* `From`/`To` without an offset is only normalised on the downsampling path so far; on the other stream-data paths it reaches the storage layer as `Unspecified` and is read as host-local — tracked as a separate fix). A path resolving to nothing falls back to the persisted value with a warning, while a present but non-date (resp. non-integer) value fails the node rather than silently widening the range. `Skip`/`Take` map onto the paginated read (offset / page size) for simple queries and are ignored by the stream-data aggregation / grouped-aggregation paths. **Downsampling** (AB#4195/AB#4233) runs `ExecuteDownsamplingQueryAsync`: `From`/`To` plus a positive `Limit` — here the *bucket count*, not a row cap — are mandatory and validated on the node (so a misconfiguration is a pipeline exception, not a storage-layer error), as is a non-empty column list; `Skip`/`Take` page the bins **in memory** because the generated bin axis ignores offset/page size; and the engine clamps the bucket count down to the distinct source bins in range (AB#4246). **Resolution-aware archive selection** (AB#4290) is inherent to this query type — there is no switch: every downsampling query is routed through `SeriesResolutionService(GetArchiveRuntimeStore(), new RollupDependencyGraph(GetRollupArchiveRuntimeStore()))` — composed per tenant, exactly as the asset-repo GraphQL `streamData.resolveSeriesQuery` and the MCP `resolve_series_query` do — using the effective `Limit` as target points and the first column's attribute path as the source path. The required aggregation is never guessed: it is the first column's persisted aggregation, or the node's optional `Aggregation` override, which mirrors what a query definition can carry (Count/Minimum/Maximum/Average/Sum) and — when set — replaces the aggregation on *every* column so the values read back match the rollup that was selected. **The result never depends on which archive was read** — `From`/`To`/`Limit` are executed verbatim (the bin geometry is never fitted to the rollup), and a rollup is only accepted when it answers the query *identically* to the persisted archive, so the node and the Refinery Studio can never disagree. Accepted only when all of: the bin width `(To-From)/Limit` is a whole multiple of the rollup's `BucketSize`; `From` sits on the rollup's tick-anchored bucket grid; the alignment is `FixedSize` (calendar rungs can't line up with fixed-width bins); and `LastAggregatedBucketEnd >= To`. Otherwise the persisted archive is read and the failing condition is warned — it is nearly always a fixable query detail. Field example (AB#4725): 7 days / **10** buckets = 16 h 48 min bins over an hourly rollup drops one hour per bin (8 of 168 hours, −4.1 % total, −27 % on a single bin); 7 days / **12** buckets = 14 h bins uses the rollup and matches exactly — pick a `Limit` that divides the range into whole multiples of the bucket size. The archive actually queried is logged at info level with bin width and bucket size. Every non-`Ok` signal warns too, and the resolver's point count never overrides the query's bucket count; `EmptyLadder` / no rollup store fall back to the persisted archive. The node exposes no time-zone option, so the resolver resolves in UTC — irrelevant in practice, since the exactness check rejects calendar-aligned rungs anyway and only fixed-size ones are ever read. See Azure DevOps AB#4195, AB#4233, AB#4290.
   - BackfillFromRtEntityNode
   - GetAssociationTargetsNode

2. **Transform Nodes** (`src/MeshAdapter.Sdk/Nodes/Transform/`): Data processing nodes
   - DataMappingNode
   - JoinNode
   - FilterLatestUpdateInfoNode
   - MakeHttpRequestNode
   - ImportFromExcelNode
   - PdfOcrExtractionNode — for PDF input, extracts the embedded text layer first (PdfPig) and falls back to raster+Tesseract OCR (IronOCR) only for scans/image PDFs or when the text layer is below `MinTextLayerChars`. The text layer is exact where OCR is lossy (separator-less codes like invoice numbers, non-German diacritics). `PreferTextLayer` (default on) is bypassed when `ExtractTables`/`ExtractBarcodes` is requested (those need the OCR path). AB#4528.
   - GenerateAndStoreReportNode
   - **RenderDataSheetPdfNode** (`RenderDataSheetPdf@1`) — renders a generic structured data sheet (title, subtitle, labelled sections, optional footer note) to a base64 PDF via QuestPDF (Community license set in the node). Domain-agnostic: the model is assembled by the pipeline. Used for the accounting BMD handover cover sheet.
   - **MergePdfNode** (`MergePdf@1`) — concatenates an ordered array of base64 PDFs into one (PdfSharp). Skips unreadable PDFs with a warning unless `FailOnInvalidPdf`. Used to prepend the cover sheet to the original document.
   - **RenderHtmlPdfNode** (`RenderHtmlPdf@1`) — renders an HTML (or plain-text) document to a base64 PDF via AngleSharp (parsing) + QuestPDF (layout). Browser-free and cross-platform; supports a pragmatic HTML subset (headings, paragraphs, `<br>`, bold/italic/underline, links, ordered/unordered lists, tables, blockquotes, `<pre>`, `<hr>`, inline `data:`-URI images — image dimensions read from PNG/GIF/JPEG/BMP headers). Optional `Title`/`TitlePath` heading and `IsHtml`/`IsHtmlPath` override (auto-detects markup otherwise). Used by the accounting email import to turn a forwarded mail that carries no PDF attachment into a receipt from its body.
   - **CreateZipArchiveNode** (`CreateZipArchive@1`) — bundles `{ fileName, contentBase64 }` entries into a base64 ZIP (`System.IO.Compression`); `fileName` may contain `/` for folders (e.g. AP/AR grouping). An entry may instead carry `pathSegments` (array of folder names ending with the file name; each segment sanitized for file-system use, so data-derived values such as vendor names cannot create unintended folders). Config `appendSequenceNumber` inserts a running `_001` number before each content entry's extension (unique names); config `manifestFileName` (+ `manifestFileNameColumn`, `manifestDelimiter`) writes a CSV manifest of all content entries (per-entry `manifest` object fields + final archive path) as the first archive entry — semicolon-separated, UTF-8 with BOM (BMD/Excel-friendly).
   - **ApplyDataPointMappingsNode** — Evaluates `System.Communication/DataPointMapping` entities for a source entity, applies mXparser expressions, produces update items for target entities. Supports state-name filtering via `sourceStateNamePath`. See [DataPointMapping concept](../octo-communication-controller-services/docs/concepts/DataPointMapping.md).
   - **BuildMappingTargetsNode** — Resolves all active DataPointMappings into `MappingTarget` records for data acquisition. Generic for any adapter (Loxone, MQTT, OPC-UA, Modbus). Supports sub-state resolution via RecordArray lookup.
   - **ExportDataPointMappingsNode** — Serialises the tenant's DataPointMappings into a portable document keyed by NATURAL identities (configurable identity attribute per CK type, e.g. `Loxone/Control → LoxoneUuid`, plus entity name; RtIds only as same-tenant hint), so mappings survive tenant re-initialisation. Optional `excludeNameRegex` exports only the manual delta (rule-generated mappings follow the deterministic `ruleId|rtId|state` name pattern and are reproducible via the generation pipeline).
   - **ImportDataPointMappingsNode** — Resolves an export document back to entities (per endpoint: RtId → identity attribute → unique name) and emits the GenerateDataPointMappings suggestion shape plus an `enabled` field, so the SAME downstream ForEach (GetOrCreate + CreateUpdateInfo + CreateAssociationUpdate + ApplyChanges) persists imported mappings. Unresolved entries are never guessed — they land in the `statisticsTargetPath` report for manual follow-up.
   - **MapToRecordArrayNode** — Converts a JSON key/value map into a CK RecordArray. Configurable `ckRecordId`, `keyAttributeName`, `valueAttributeName`.
   - **UpdateRecordArrayItemNode** — Reconstruction-style update of a single item inside a CK RecordArray on a runtime entity. The node rebuilds the array from the existing items plus the patched item rather than mutating in place, which keeps it consistent with the path-only `IDataContext` write model and avoids aliased mutation across sub-contexts.

   Transform nodes that need multi-match read/write semantics consume `IDataContext.UpdateMatchesAsync(jsonPath, body)` (per-match sub-contexts, path-only). Read-only multi-match uses `IDataContext.SelectMatches(jsonPath)`, which returns an `IEnumerable<IDataContext>` of detached sub-contexts — one per match — replacing the former `EnumerateMatches` that returned raw `JsonNode?` values.

3. **Load Nodes** (`src/MeshAdapter.Sdk/Nodes/Load/`): Data persistence nodes
   - ApplyChangesNode/ApplyChangesNode2
   - SaveStreamDataInArchive
   - EMailSenderNode
   - SftpUploadNode
   - **DeployPipelineNode** — Deploys a specific pipeline within the same data flow via the Communication Controller REST API. Uses `ServiceAccountConfiguration` for OAuth2 authentication. Safety: cannot deploy self, must be in same data flow.
   - **TeamsBotReplyNode** (`TeamsBotReply@1`) — sends a reply into a Microsoft Teams conversation via the Bot Framework REST API (`POST {serviceUrl}/v3/conversations/{conversationId}/activities`). Bot token via client-credentials against the `botframework.com` authority; credentials read from a `MicrosoftGraphConfiguration` (its ClientId/ClientSecret double as the bot App ID/secret). Outbound counterpart of `FromTeamsBot@1`.

4. **Trigger Nodes** (`src/MeshAdapter.Sdk/Nodes/Trigger/`): Pipeline initiation nodes
   - FromHttpRequestNode
   - FromWatchRtEntityNode
   - FromExecutePipelineCommandNode
   - FromSendNotificationNode
   - FromEmailNode (IMAP folder polling via MailKit)
   - FromMicrosoftGraphNode (Teams channel polling via Microsoft Graph)
   - **FromTeamsBotNode** (`FromTeamsBot@1`) — hosts the Bot Framework messaging endpoint `POST /{tenant}/teamsBot` (via `IHttpRequestService`), parses the inbound Teams activity, downloads file attachments (1:1 `application/vnd.microsoft.teams.file.download.info` via pre-authenticated URL; channel `reference` via Microsoft Graph SharePoint share), and emits the `EmailData`/`AttachmentData` shape at `$.Emails` plus conversation routing at `$.Conversation` (serviceUrl/conversationId/activityId/from) for `TeamsBotReply@1`. Credentials read from `MicrosoftGraphConfiguration`. Inbound JWT check via `ValidateInboundToken` (default false; validates aud+exp only — NOT the signature yet, harden before public exposure). Requires `HttpRequestService` to surface request headers (`input["headers"]`).
   - **FromMicrosoftGraphEmailNode** — Polls an Office 365 mailbox FOLDER (path like `Archive/Invoices/ToDo`, '/'-separated, resolved from the mailbox root — never the inbox unless configured) via Microsoft Graph client credentials. Executes the pipeline ONCE PER MESSAGE (batch of one `EmailData`) so success maps 1:1 to the per-message action: on success the mail is moved to `moveToFolderPathOnSuccess` (leaf folder auto-created); on failure it stays in the source folder and is retried up to `maxAttemptsPerMessage` times per adapter lifetime. Only `fileAttachment` contents are downloaded (item/reference attachments skipped). Requires Graph application permission `Mail.ReadWrite`.

### Core Services

- **MeshAdapterService**: Main service handling adapter startup/shutdown and pipeline registration.
  Also implements `IAdapterService.CkModelChangedAsync` (AB#4456): when the communication controller
  broadcasts a CK model change (after `ImportCk` / `ClearCache`), the tenant's CK cache is unloaded
  and lazily reloaded on the next pipeline execution — without this, the load-once CK cache
  (`ModelLoaderService` guard) would keep validating pipeline writes (`CreateUpdateInfo@1` /
  `ApplyChanges@2`) against the old model until the process restarts.
- **MeshEtlContext**: ETL context implementation providing access to repositories and pipeline state
- **HttpRequestService**: Handles dynamic HTTP routing and request processing
- **MeshContextCreatorService**: Creates contexts for pipeline execution
- **ServiceAccountTokenService**: Acquires OAuth2 tokens from `ServiceAccountConfiguration` entities for service-to-service REST calls (used by `DeployPipelineNode`)

### JSON / Serialization (System.Text.Json)

The adapter and all ~35 nodes are System.Text.Json-only on the pipeline data path. Newtonsoft is no longer used for pipeline data flow (it may still appear in unrelated transports such as SignalR contracts).

- **`SystemTextJsonOptions.Default`** (from `octo-sdk`, `src/Sdk.Common/EtlDataPipeline/SystemTextJsonOptions.cs`) — central `JsonSerializerOptions` carrying the STJ converters required by OctoMesh runtime types. The mesh-adapter no longer maintains its own bundle; all nodes that need to round-trip runtime entities, mutation DTOs, etc. reuse this single options instance from the SDK.
- **Newtonsoft-parity contract.** The numeric/scalar round-trip rules (`int` preference, `.0` emission for integral doubles/floats/decimals, `JsonScalar.ToClr` boxing) are enforced by `Sdk.Common.PipelineParityTests` in octo-sdk — Newtonsoft is the oracle. If a node consumer pattern-matches on `long` for an attribute value (e.g. `MinMaxNode`'s comparable-value switch), it must also handle `int`; values that fit in Int32 stay Int32 after the round-trip. See `octo-construction-kit-engine/CLAUDE.md` for the full serialization rules.
- The pipeline data context is the path-only `IDataContext` from `octo-sdk` — see the spec at `octo-sdk/docs/superpowers/specs/2026-05-06-newtonsoft-to-stj-pipeline-migration-design.md` §5. Nodes do not see `JToken`/`JObject`/`JArray` on the data flow surface; they operate via:
  - `Get<T>(path)` / `GetValue(path)` / `TryGet<T>(path, out value)` — typed scalar reads
  - `Set<T>(path, value, ...)` — typed writes; report builders use `Set<T>` with typed records instead of constructing `JsonObject` manually
  - `WriteJsonTo(path, stream)` — serialize a subtree to a stream (used for hashing, e.g. `CheckDuplicateNode` / `ApplyDataPointMappingsNode`); its `DataContextImpl` impl routes the `Utf8JsonWriter` through `SystemTextJsonOptions.Default.Encoder` (`UnsafeRelaxedJsonEscaping`) so the bytes match Newtonsoft on non-ASCII/HTML — load-bearing precisely because these consumers **hash** the output
  - `Iterate*Async(path, body)` — iteration over arrays
  - `UpdateMatchesAsync(jsonPath, body)` — multi-match read/write (per-match sub-contexts)
  - `SelectMatches(jsonPath)` — read-only multi-match; returns `IEnumerable<IDataContext>` of detached sub-contexts, one per JSONPath match (replaces the removed `EnumerateMatches` which returned raw `JsonNode?` values)
- `JsonSerializerOptions` may only appear in nodes for non-data-flow purposes (e.g. HTTP API calls, prompt serialization). Node-author code must not pass `JsonSerializerOptions` to any `IDataContext` method — all STJ details are internal to the context implementation.

### Configuration

The solution uses:
- **Directory.Build.props**: Central MSBuild configuration
- Three build configurations: Debug, Release, DebugL (for local development)
- Target framework: .NET 10.0
- OctoVersion: Managed via Directory.Build.props (3.2.* for public, 0.1.* for private server)

### Key Dependencies

- Meshmakers.Octo.Sdk.* packages (various SDK components)
- IronOCR for PDF text extraction
- AngleSharp for HTML parsing (`RenderHtmlPdf`); QuestPDF + PdfSharp for PDF generation/merge
- MongoDB for data persistence
- SignalR for real-time communication

## Pipeline Schema Generation

The build automatically generates a `pipeline-schema.json` file in the build output directory. This JSON Schema describes all available pipeline node configurations and can be used for editor autocompletion and validation.

- **Output**: `pipeline-schema.json` in the build output directory
- **Trigger**: The `GeneratePipelineSchema` MSBuild target runs after Build via `dotnet exec "$(TargetPath)" --generate-pipeline-schema <output-path>`
- **Incremental**: Only regenerates when the binary changes
- **Opt-out**: Set MSBuild property `GeneratePipelineSchema=false` to disable

## Development Notes

- All projects have nullable reference types enabled
- Warnings are treated as errors
- Implicit usings are enabled
- The solution follows a node-based pipeline architecture where each node has a configuration class in MeshNodes.Sdk and an implementation in MeshAdapter.Sdk