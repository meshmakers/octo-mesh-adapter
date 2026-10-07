# Octo Mesh Adapter

[![Build Status](https://dev.azure.com/meshmakers/OctoMesh/_apis/build/status%2Fplugs%2Focto-mesh-adapter-CI?branchName=main)](https://dev.azure.com/meshmakers/OctoMesh/_build/latest?definitionId=112&branchName=main)

An ETL (Extract-Transform-Load) pipeline execution engine that manages and executes mesh pipelines. Built on .NET 10.0 with a flexible, node-based architecture for creating data processing workflows.

## Features

- **Data Extraction**: Retrieve entities from MongoDB, execute queries, enrich data from external sources
- **Data Transformation**: Map values, process documents (Excel, PDF with OCR), integrate AI services
- **Data Loading**: Persist changes to MongoDB, store time-series data in CrateDB, send email notifications
- **Event-Driven Triggers**: HTTP endpoints, entity watchers, command bus, email reception

## Quick Start

### Prerequisites

- .NET 10.0 SDK
- MongoDB
- CrateDB (optional, for time-series data)

### Build

```bash
# Build the solution
dotnet build

# Build in Release mode
dotnet build -c Release

# Build in DebugL mode (uses local NuGet packages from ../nuget)
dotnet build -c DebugL
```

### Run

```bash
dotnet run --project src/MeshAdapter/MeshAdapter.csproj
```

## Project Structure

```
octo-mesh-adapter/
├── src/
│   ├── MeshAdapter/           # Main executable service
│   ├── MeshAdapter.Sdk/       # SDK with pipeline nodes and services
│   └── MeshNodes.Sdk/         # Node configuration definitions
├── tests/                     # Unit tests
└── docs/                      # Documentation
```

## Pipeline Nodes

The adapter provides four categories of pipeline nodes:

| Category      | Purpose             | Examples                                                        |
|---------------|---------------------|-----------------------------------------------------------------|
| **Extract**   | Data retrieval      | GetRtEntitiesById, GetRtEntitiesByType, GetAssociationTargets   |
| **Transform** | Data processing     | DataMapping, MakeHttpRequest, PdfOcrExtraction, AnthropicAiQuery|
| **Load**      | Data persistence    | ApplyChanges, SaveStreamDataInArchive, EMailSender                     |
| **Trigger**   | Pipeline initiation | FromHttpRequest, FromWatchRtEntity, FromEmail                   |

## Documentation

- [Developer Guide](docs/developer-guide.md) - Architecture, nodes, services, and configuration
- [Test Concept](docs/test-concept.md) - Unit and integration testing strategy
- [Integration Test Concept](docs/integration-test-concept.md) - Testcontainers-based integration testing
- [PDF OCR Extraction](docs/pdf-ocr-extraction.md) - PDF text extraction with IronOCR

### Examples

- [Email Trigger](docs/examples/email-trigger.md) - Configure email-triggered pipelines
- [Binary Upload](docs/examples/binary-upload.md) - HTTP binary file upload handling

### API reference

`scripts/createDocumentation.ps1` generates the published API reference from the XML documentation
of `Meshmakers.Octo.MeshAdapter.Nodes` and uploads it as a build artifact. The `MeshAdapter` host
executable is deliberately not documented: it exposes no public API of its own, only the `Program`
class the compiler generates for top-level statement apps.

## Helm chart: SECRET attribute key ring (AB#5536)

The runtime engine binds `SecretEncryption:Keys:<kid>`, `SecretEncryption:ActiveKeyId` and
`SecretEncryption:LegacyV1Key` (concept `octo-construction-kit-engine/docs/concept-secret-attribute-type.md`
§3.5). The chart in `src/charts/octo-mesh-adapter` renders them from
`secrets.secretEncryption.{keys,activeKeyId,legacyV1Key}` as `OCTO_SECRETENCRYPTION__KEYS__<kid>`,
`OCTO_SECRETENCRYPTION__ACTIVEKEYID` and `OCTO_SECRETENCRYPTION__LEGACYV1KEY` (inside
`octo-mesh.system-env`).

- The communication operator supplies them for `ReceivesClusterSecrets=true` workloads: keys and
  the legacy key as `valueFrom` maps into `{release}-octo-secrets`, the active key id as a plain string.
- Optional: nothing is rendered when unset, the adapter starts, and only SECRET attribute access fails.
- The key id keeps its case in the variable name (lowercase, validated) because it is the id in the
  `enc:v2:<kid>:` header.
- An empty `activeKeyId` with exactly one key selects that key; with several keys it fails the render.

## License

Proprietary - Meshmakers
