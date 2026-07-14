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
   - **GetQueryByIdNode** — Executes a persisted query by RtId. Resolves the shared `RtPersistentQuery` base, so the caller does not need to know the kind in advance. Supports runtime-data queries (`RtSimpleRtQuery`, `RtAggregationRtQuery`, `RtGroupingAggregationRtQuery`) via `TenantRepository.GetRtEntitiesGraphByTypeAsync`, and **simple stream-data queries** (`RtSimpleSdQuery`) via the tenant's `IStreamDataRepository` (resolved through `ISystemContext.FindTenantContextAsync(tenantId).GetStreamDataRepository()`, same pattern as `SaveStreamDataInArchive`). Stream-data result-shape: a simple SD query returns a **time series** mapped into `QueryResult` with a leading `Timestamp` column followed by the projected attribute columns (differs from the runtime simple query, which is one row per entity with no timestamp). Optional `From`/`To`/`Limit` config values override the time range / row cap persisted on the query; `Skip`/`Take` map onto the paginated read (offset / page size). Aggregated / grouped-aggregated stream-data queries (`RtAggregationSdQuery`, `RtGroupingAggregationSdQuery`, `RtDownsamplingSdQuery`) are **not yet supported** and surface as `UnsupportedQueryType`. See Azure DevOps AB#4195.
   - BackfillFromRtEntityNode
   - GetAssociationTargetsNode

2. **Transform Nodes** (`src/MeshAdapter.Sdk/Nodes/Transform/`): Data processing nodes
   - DataMappingNode
   - JoinNode
   - FilterLatestUpdateInfoNode
   - MakeHttpRequestNode
   - ImportFromExcelNode
   - PdfOcrExtractionNode (uses IronOCR for PDF processing)
   - GenerateAndStoreReportNode
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

4. **Trigger Nodes** (`src/MeshAdapter.Sdk/Nodes/Trigger/`): Pipeline initiation nodes
   - FromHttpRequestNode
   - FromWatchRtEntityNode
   - FromExecutePipelineCommandNode
   - FromSendNotificationNode

### Core Services

- **MeshAdapterService**: Main service handling adapter startup/shutdown and pipeline registration
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