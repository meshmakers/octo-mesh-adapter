# Octo Mesh Adapter - Developer Documentation

This document provides comprehensive documentation of the Octo Mesh Adapter's functionality, architecture, and configuration options for developers.

## Table of Contents

1. [Overview](#overview)
2. [Architecture](#architecture)
3. [Pipeline Nodes](#pipeline-nodes)
   - [Extract Nodes](#extract-nodes)
   - [Transform Nodes](#transform-nodes)
   - [Load Nodes](#load-nodes)
   - [Trigger Nodes](#trigger-nodes)
4. [Core Services](#core-services)
5. [Pipeline Execution Flow](#pipeline-execution-flow)
6. [Configuration](#configuration)
7. [HTTP API Handling](#http-api-handling)
8. [Additional Features](#additional-features)

---

## Overview

The Octo Mesh Adapter is an ETL (Extract-Transform-Load) pipeline execution engine built on .NET 10.0. It provides a flexible, node-based architecture for creating data processing workflows that can be triggered by various events including HTTP requests, entity changes, commands, and notifications.

### Key Capabilities

- **Data Extraction**: Retrieve entities from MongoDB, execute queries, and enrich data from external sources
- **Data Transformation**: Map values, create update information, process documents (Excel, PDF), integrate AI services
- **Data Loading**: Persist changes to MongoDB, store time-series data in CrateDB, send email notifications
- **Event-Driven Triggers**: HTTP endpoints, entity watchers, command bus, email reception

---

## Architecture

### Project Structure

```
octo-mesh-adapter/
├── src/
│   ├── MeshAdapter/                    # Main executable service
│   │   └── Program.cs                  # Startup & configuration
│   ├── MeshAdapter.Sdk/                # SDK implementation
│   │   ├── Nodes/                      # Pipeline node implementations
│   │   │   ├── Extract/                # Data retrieval nodes
│   │   │   ├── Transform/              # Data processing nodes
│   │   │   ├── Load/                   # Persistence nodes
│   │   │   └── Trigger/                # Pipeline triggers
│   │   ├── Services/                   # Core services
│   │   ├── Configuration/              # DI & config
│   │   ├── Middlewares/                # HTTP handling
│   │   └── Common/                     # Utilities
│   └── MeshNodes.Sdk/                  # Node configuration definitions
│       ├── Extract/                    # Extract node configs
│       ├── Transform/                  # Transform node configs
│       ├── Load/                       # Load node configs
│       ├── Trigger/                    # Trigger node configs
│       └── PipelineDataTransferObjects/# Common DTOs
└── tests/                              # Unit tests
```

### Node Architecture Pattern

Each pipeline node follows a consistent pattern:
- **Configuration Class** (`MeshNodes.Sdk`): Defines the node's configurable parameters
- **Implementation Class** (`MeshAdapter.Sdk`): Contains the execution logic

---

## Pipeline Nodes

### Extract Nodes

Extract nodes retrieve data from various sources and make it available for pipeline processing.

#### GetRtEntitiesByIdNode

Retrieves runtime entities by their unique identifiers.

| Parameter | Type | Description |
|-----------|------|-------------|
| `CkTypeId` / `CkTypeIdPath` | string | Type identifier (static or path) |
| `RtIds` | ICollection | List of runtime entity IDs |
| `RtIdsPath` | string | JSON path to runtime IDs |
| `FieldFilters` | ICollection | Optional field filtering |
| `Skip` / `Take` | int | Pagination parameters |

**Output**: Retrieved entities at configured target path.

#### GetRtEntitiesByTypeNode

Retrieves all runtime entities of a specified type.

| Parameter | Type | Description |
|-----------|------|-------------|
| `CkTypeId` / `CkTypeIdPath` | string | Type identifier |
| `FieldFilters` | ICollection | Field-based filtering |
| `SortOrders` | ICollection | Result sorting |
| `Skip` / `Take` | int | Pagination |

#### GetAssociationTargetsNode

Fetches entities related through associations.

| Parameter | Type | Description |
|-----------|------|-------------|
| `OriginRtId` / `OriginRtIdPath` | string | Source entity ID |
| `OriginCkTypeId` / `OriginCkTypeIdPath` | string | Source type |
| `TargetCkTypeId` / `TargetCkTypeIdPath` | string | Target type |
| `AssociationRoleId` / `AssociationRoleIdPath` | string | Relationship role |
| `GraphDirection` | enum | Inbound / Outbound / Any |
| `FieldFilters` / `SortOrders` | ICollection | Query refinement |

**Output**: Multi-entity result with association mapping.

#### GetRtEntitiesByWellKnownNameTypeNode

Retrieves entities by semantic well-known name identifiers.

#### GetOrCreateRtEntitiesByTypeNode

Fetches existing entities or creates them if not found (idempotent retrieval).

#### GetQueryByIdNode

Executes pre-defined queries stored in the system.

#### EnrichWithMongoDataNode

Supplements entities with additional data from MongoDB.

#### GetNotificationTemplateNode

Retrieves notification templates for email and message generation.

---

### Transform Nodes

Transform nodes process, modify, and enrich data within the pipeline.

#### DataMappingNode

Maps values from source to target types with configurable mapping rules.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | Source path for value extraction |
| `SourceValueType` | enum | Original data type (Int, String, Binary, Boolean, DateTime, Double, TimeSpan) |
| `TargetValueType` | enum | Desired output data type |
| `Mappings` | ICollection | Source-to-target value mappings |

#### CreateUpdateInfoNode

Constructs update information objects for entity persistence.

| Parameter | Type | Description |
|-----------|------|-------------|
| `RtId` / `RtIdPath` | string | Entity identifier |
| `CkTypeId` / `CkTypeIdPath` | string | Entity type |
| `UpdateKind` | enum | Insert, Update, or Delete |
| `AttributeUpdates` | ICollection | Field updates to apply |
| `RtWellKnownName` / `RtWellKnownNamePath` | string | Semantic name |
| `TimestampPath` | string | Optional timestamp override |

**Output**: `EntityUpdateInfo` object for persistence operations.

#### CreateAssociationUpdateNode

Creates association updates between entities.

| Parameter | Type | Description |
|-----------|------|-------------|
| `OriginRtId` / `OriginRtIdPath` | string | Source entity |
| `TargetRtId` / `TargetRtIdPath` | string | Target entity |
| `AssociationRoleId` / `AssociationRoleIdPath` | string | Relationship type |
| `UpdateKind` | enum | Create or Delete association |

**Output**: `AssociationUpdateInfo` for relation persistence.

#### MakeHttpRequestNode

Executes HTTP requests to external services.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Method` | enum | HTTP method (GET, POST, PUT, DELETE) |
| `Url` / `UrlPath` | string | Target endpoint |
| `Body` / `BodyPath` | string | Request body (JSON) |
| `HeaderParameters` | ICollection | HTTP headers with dynamic replacement |
| `PathParameters` | ICollection | URL path parameter substitution |
| `TargetPath` | string | Response storage location |

**Features**: Dynamic header/path parameter substitution, JSON/text body support, response parsing.

#### ImportFromExcelNode

Parses and imports hierarchical data from Excel files.

| Parameter | Type | Description |
|-----------|------|-------------|
| Import Type | enum | TreePath (hierarchical by path) or TreeColumn (parent-child columns) |
| Column Mapping | ICollection | Column to field mapping |
| Root Node | string | Root node specification |

**Features**: Hierarchical entity parsing, parent-child relationship establishment, well-known name resolution.

#### PdfOcrExtractionNode

Extracts text from PDF files using IronOCR.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | Base64-encoded PDF data path |
| `Language` | string | OCR language setting |
| `PageNumbers` | ICollection | Specific pages to process |

**Constraint**: Maximum 1MB file size.

#### AnthropicAiQueryNode

Processes content using Claude AI API.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | Main content path |
| `Question` | string | Query to ask Claude |
| `DataPaths` | ICollection | Additional context data |
| `ApiKey` | string | Anthropic API key |
| `TargetPath` | string | Response storage location |

#### StatisticalAnomalyNode

Detects anomalies using statistical methods.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | Value path to monitor |
| `GroupByPath` | string | Optional grouping field |
| `ContextPath` | string | Additional context data |
| `Method` | enum | Z-Score, IQR, PercentChange, MovingAverage |
| `Threshold` | double | Detection sensitivity |
| `MinSamples` / `MaxSamples` | int | Stateful monitoring parameters |
| `WindowSize` | int | Moving average window |
| `ResetStatistics` | bool | Stateless vs. stateful mode |

**Detection Methods**:
- **Z-Score**: Threshold in standard deviations (default 3.0)
- **IQR**: Interquartile range-based (threshold = multiplier)
- **PercentChange**: Change from last value (threshold = percent)
- **MovingAverage**: Deviation from moving average (threshold = percent)

#### MachineLearningAnomalyNode

Advanced ML-based anomaly detection for complex pattern detection.

#### DistinctNode

Removes duplicate objects from arrays.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | Array path |
| `DistinctValuePath` | string | Field to check for uniqueness |
| `TargetPath` | string | Output location |

#### FilterLatestUpdateInfoNode

Filters to keep only the latest updates per entity, avoiding duplicate updates.

#### PlaceholderReplaceNode

String template substitution with variable replacement for dynamic string generation.

#### QueryResultToMarkdownTableNode

Formats query results as Markdown tables for report generation.

#### GenerateAndStoreReportNode

Creates and persists reports from pipeline data.

#### CreateFileSystemItemUpdateNode

Creates file system-based update information for file-based entity tracking.

---

### Load Nodes

Load nodes persist data to various storage systems.

#### ApplyChangesNode

Applies entity updates to MongoDB.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | Location of EntityUpdateInfo collection |

**Features**:
- Transaction management with retry logic (5 attempts)
- Semaphore-based concurrency control
- Write-conflict handling (MongoDB error code 112)
- Duplicate deduplication (keeps latest update only)
- Automatic operation result validation

**Supported Operations**: Insert, Update, Replace, Delete

#### ApplyChangesNode2

Alternative implementation with different transaction strategy.

#### SaveInTimeSeriesNode

Persists entity data to CrateDB time-series database.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | EntityUpdateInfo collection path |

**Stored Data**:
- Timestamp (entity change time or external timestamp)
- RtId (entity identifier)
- RtWellKnownName (semantic name)
- CkTypeId (type identifier)
- Attributes (entity field values)

#### EMailSenderNode

Sends emails with optional Markdown-to-HTML conversion.

| Parameter | Type | Description |
|-----------|------|-------------|
| `ServerConfiguration` | string | Global config reference |
| `ToPath` | string | Recipient email addresses |
| `SubjectPath` | string | Email subject |
| `BodyPath` | string | Email body (supports Markdown) |
| `FromPath` | string | Sender override (optional) |

**Features**:
- SMTP configuration (host, port, SSL)
- Concurrent email limit control (semaphore-based)
- Markdown to HTML conversion
- Multiple recipients support

---

### Trigger Nodes

Trigger nodes initiate pipeline execution in response to events.

#### FromHttpRequestNode

Triggers pipeline via HTTP requests.

| Parameter | Type | Description |
|-----------|------|-------------|
| `Path` | string | HTTP endpoint path |
| `Method` | enum | HTTP method (GET, POST, PUT, DELETE) |

**Features**:
- Dynamic route registration
- Request body/query/header parsing
- JSON and multipart form-data support
- Base64 encoding for binary data
- File upload handling

**Request Input Structure**:
```json
{
  "path": "...",
  "method": "...",
  "body": "...",
  "query": {...},
  "files": [...],
  "formData": {...}
}
```

#### FromWatchRtEntityNode

Triggers when entities are created, updated, or deleted.

| Parameter | Type | Description |
|-----------|------|-------------|
| `CkTypeId` | string | Entity type to monitor |
| `RtId` | string | Specific entity (optional) |
| `UpdateTypes` | enum | Insert, Update, Delete, Replace |
| `FieldFilters` | ICollection | Filter by field values (post-update) |
| `BeforeFieldFilters` | ICollection | Filter by previous values (pre-update) |

**Features**: Real-time MongoDB change stream monitoring.

#### FromExecutePipelineCommandNode

Triggers via command bus (MassTransit/EventHub).

**Features**:
- Command consumer registration
- Queue-based message handling
- Async command processing
- Response callback mechanism

#### FromPipelineTriggerEventNode

Triggers on specific system events for event-driven architectures.

#### FromSendNotificationNode

Triggers on notification events for notification processing workflows.

#### FromEmailNode

Triggers on incoming emails for email-driven workflows.

---

## Core Services

### MeshEtlContext

**File**: `src/MeshAdapter.Sdk/Services/MeshEtlContext.cs`

The ETL context providing access to repositories and pipeline state.

| Property | Description |
|----------|-------------|
| `TenantRepository` | MongoDB tenant data access |
| `TenantId` | Current tenant identifier |
| `PipelineExecutionId` | Unique execution GUID |
| `ExternalReceivedDateTime` | External system timestamp |
| `GlobalConfiguration` | Pipeline-wide config access |
| `Properties` | Stage-shared data dictionary |

### HttpRequestService

**File**: `src/MeshAdapter.Sdk/Services/HttpRequests/HttpRequestService.cs`

Manages dynamic HTTP route registration and request processing.

| Method | Description |
|--------|-------------|
| `CreateRoute()` | Register HTTP endpoint |
| `RemoveRoute()` | Unregister endpoint |
| `SendRequestAsync()` | Process incoming request |

**Supported Content Types**:
- JSON (parsed to JObject)
- Plain text (string)
- Multipart/form-data (files + fields)
- Binary data (base64 encoded)

### MeshContextCreatorService

**File**: `src/MeshAdapter.Sdk/Services/MeshContextCreatorService.cs`

Creates ETL and trigger contexts for pipeline execution.

**Context Creation Flow**:
1. Load tenant repository
2. Load CK cache for tenant
3. Create context with all configuration
4. Return typed context

### MeshAdapterTriggerContext

**File**: `src/MeshAdapter.Sdk/Services/MeshAdapterTriggerContext.cs`

Manages trigger-initiated pipeline execution.

| Method | Description |
|--------|-------------|
| `StartExecutePipelineAsync()` | Begin pipeline run |

---

## Pipeline Execution Flow

```
HTTP Request / Event Trigger
        ↓
FromHttpRequestNode / FromWatchRtEntityNode (etc.)
        ↓
MeshAdapterTriggerContext.StartExecutePipelineAsync()
        ↓
Create EtlContext (via MeshContextCreatorService)
        ↓
IEtlDataOrchestrator.ExecutePipelineAsync()
        ↓
Execute Node Pipeline:
  ├─ Extract Nodes (GetRtEntities*, GetAssociationTargets, etc.)
  ├─ Transform Nodes (DataMapping, CreateUpdateInfo, MakeHttpRequest, etc.)
  └─ Load Nodes (ApplyChanges, SaveInTimeSeries, EMailSender)
        ↓
Return Result / Store in Time-Series / Send Notifications
```

**Note**: The `Properties` dictionary in the context carries state across all nodes.

---

## Configuration

### MeshAdapterConfiguration

**Configuration Section**: `"Adapter"`

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ReportingServiceUrl` | string | `https://localhost:5007` | Report service endpoint |
| `StreamDataHost` | string | `127.0.0.1` | CrateDB hostname |
| `StreamDataUser` | string | `crate` | CrateDB user |
| `StreamDataPassword` | string | (empty) | CrateDB password |

### Build Configurations

| Configuration | Description |
|---------------|-------------|
| `Debug` | Standard debug build |
| `Release` | Optimized release build |
| `DebugL` | Local development (uses NuGet packages from `../nuget`) |

### Dependency Injection

All components are registered via `ServiceCollectionExtensions.cs`:
- All pipeline nodes (Extract, Transform, Load, Trigger)
- HttpRequestService (singleton)
- MeshContextCreatorService (singleton)
- WellKnownNameLoader (scoped)
- RuntimeEngine with MongoDB repository
- StreamData database client

---

## HTTP API Handling

### Dynamic Route Registration

- **Middleware**: `DynamicRouteMiddleware`
- **Service**: `IHttpRequestService`
- **Route Key**: `{TenantId}/{Path}` with uppercase HTTP method

### Request Processing Flow

1. Extract path and method from HTTP context
2. Lookup registered route in internal dictionary
3. Parse request body based on Content-Type
4. Build input JObject with path, method, body, query, files, formData, contentType
5. Execute pipeline with input
6. Return JToken response as JSON

### Response Formats

| Type | Format |
|------|--------|
| Success | JSON object/array from pipeline |
| Error | `OperationFailedErrorDto` with failure details |

---

## Additional Features

### Error Handling

- **MeshAdapterPipelineExecutionException**: Custom exception with context information
- **Write-Conflict Retry**: ApplyChangesNode retries 5 times on MongoDB conflicts
- **Semaphore Concurrency**: Email sender limits concurrent emails
- **Operation Validation**: Automatic error checking on database operations

### Data Enrichment

- MongoDB to MongoDB (EnrichWithMongoDataNode)
- External API integration (MakeHttpRequestNode)
- AI-powered content analysis (AnthropicAiQueryNode)

### File Handling

- Excel import with hierarchy support
- PDF OCR extraction (IronOCR)
- Multipart file upload via HTTP
- Base64 encoding for binary transfer

### Real-Time Features

- Change stream monitoring (RxJS Observables)
- WebSocket support via SignalR
- Event-driven pipeline triggers
- Asynchronous command bus (MassTransit)

---

## Pipeline Schema Generation

The build process auto-generates a `pipeline-schema.json` file that provides a JSON Schema describing all available pipeline node configurations. This schema can be used for editor autocompletion and validation when authoring pipeline definitions.

### How It Works

- The `GeneratePipelineSchema` MSBuild target runs automatically after Build
- It executes `dotnet exec "$(TargetPath)" --generate-pipeline-schema <output-path>` to invoke the adapter's built-in schema generator
- The `NodeSchemaRegistry` discovers all registered pipeline nodes and produces a complete JSON Schema
- The schema is only regenerated when the binary changes (incremental build)

### Output

- **File**: `pipeline-schema.json` in the build output directory
- **Format**: Standard JSON Schema
- **Enum values**: All enums use CONSTANT_CASE format (e.g. `NOT_EQUALS`, `DATE_TIME`)

### Opting Out

To disable automatic schema generation, set the MSBuild property:

```xml
<PropertyGroup>
  <GeneratePipelineSchema>false</GeneratePipelineSchema>
</PropertyGroup>
```

---

## Key Technologies & Dependencies

| Technology | Purpose |
|------------|---------|
| .NET 10.0 | Runtime framework |
| MongoDB | Primary data store |
| CrateDB | Time-series database |
| IronOCR | PDF text extraction |
| Anthropic Claude | AI query processing |
| MassTransit/EventHub | Asynchronous messaging |
| Newtonsoft.Json | JSON parsing |
| Markdig | Markdown to HTML conversion |
| SignalR | Real-time communication |
