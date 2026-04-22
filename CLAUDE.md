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
   - GetQueryByIdNode
   - EnrichWithMongoDataNode
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
   - **MapToRecordArrayNode** — Converts a JSON key/value map into a CK RecordArray. Configurable `ckRecordId`, `keyAttributeName`, `valueAttributeName`.

3. **Load Nodes** (`src/MeshAdapter.Sdk/Nodes/Load/`): Data persistence nodes
   - ApplyChangesNode/ApplyChangesNode2
   - SaveInTimeSeries
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