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
| **Load**      | Data persistence    | ApplyChanges, SaveInTimeSeries, EMailSender                     |
| **Trigger**   | Pipeline initiation | FromHttpRequest, FromWatchRtEntity, FromEmail                   |

## Documentation

- [Developer Guide](docs/developer-guide.md) - Architecture, nodes, services, and configuration
- [Test Concept](docs/test-concept.md) - Unit and integration testing strategy
- [PDF OCR Extraction](docs/pdf-ocr-extraction.md) - PDF text extraction with IronOCR

### Examples

- [Email Trigger](docs/examples/email-trigger.md) - Configure email-triggered pipelines
- [Binary Upload](docs/examples/binary-upload.md) - HTTP binary file upload handling

## License

Proprietary - Meshmakers
