# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

This is the Octo Mesh Adapter project - an adapter that manages and executes mesh pipelines. It's a .NET 10.0 solution consisting of three main projects:

- **MeshAdapter**: The main executable service
- **MeshAdapter.Sdk**: SDK containing pipeline nodes and services
- **MeshNodes.Sdk**: Node configuration definitions

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/charts/**`, `tests/chart/**`, `src/MeshAdapter.Sdk/Leasing/**`, `src/MeshAdapter.Sdk/Configuration/**` | `docs/chart-credentials-pools-publishing.md` |
| `src/MeshAdapter.Sdk/Nodes/Extract/GetStreamDataNode.cs`, `src/MeshAdapter.Sdk/Nodes/Extract/AggregateStreamDataNode.cs`, `src/MeshAdapter.Sdk/Nodes/StreamData*.cs` | `docs/concept-getstreamdata-node.md` |
| `src/MeshAdapter.Sdk/Services/**`, `src/MeshAdapter/**` | `docs/core-services.md` |
| `src/MeshAdapter.Sdk/Nodes/Extract/**`, `src/MeshAdapter.Sdk/Nodes/MailFolders/**`, `src/MeshAdapter.Sdk/Nodes/StreamData*.cs` | `docs/nodes-extract.md` |
| `src/MeshAdapter.Sdk/Nodes/Load/**`, `src/MeshAdapter.Sdk/Nodes/Sftp*.cs` | `docs/nodes-load.md` |
| `src/MeshAdapter.Sdk/Nodes/Transform/**` | `docs/nodes-transform.md` |
| `src/MeshAdapter.Sdk/Nodes/Trigger/**`, `src/MeshAdapter.Sdk/Nodes/ConfigurationSettingsReader.cs` | `docs/nodes-triggers.md` |
| `src/MeshAdapter.Sdk/Nodes/Transform/PdfOcrExtractionNode.cs`, `src/MeshAdapter.Sdk/Nodes/Transform/OcrNativeMemoryCoordinator.cs` | `docs/pdf-ocr-extraction.md` |
| `src/MeshAdapter.Sdk/Services/PipelineIdentityResolver.cs`, `src/MeshAdapter.Sdk/Services/MeshEtlContext.cs`, `src/MeshAdapter.Sdk/IMeshEtlContext.cs` | `docs/pipeline-execution-identity.md` |
| `src/MeshAdapter.Sdk/Common/PlatformOnlySecrets.cs`, `src/MeshAdapter.Sdk/Common/SecretAttributes.cs`, `src/MeshAdapter.Sdk/Common/GenericCreate*.cs`, `src/MeshAdapter.Sdk/Common/RtAttributeAccess*.cs` | `docs/secrets-and-write-guards.md` |
| `Directory.Build.props`, `src/MeshAdapter.Sdk/Common/JsonNullAsDefaultAttribute.cs`, `src/MeshAdapter.Sdk/Nodes/JsonDuplicateKeys.cs` | `docs/serialization-and-build.md` |
<!-- <<< end generated: routing -->

## Build & test

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

- All projects have nullable reference types enabled
- Warnings are treated as errors
- Implicit usings are enabled

## Before you commit

> **Important**: This file must be kept up-to-date when the codebase changes. When adding new nodes, services, or modifying the architecture, update the relevant sections accordingly. Also update `docs/developer-guide.md` for comprehensive changes.

## Rules

