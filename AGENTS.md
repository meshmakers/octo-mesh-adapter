# AGENTS.md

Instructions for coding agents in this repository. Detail lives in `docs/`; read the matching file before changing the
code it covers.

## What this repo is

The Octo Mesh Adapter is a .NET 10 service that manages and executes mesh pipelines: it registers the pipelines of a
tenant, starts them through triggers (HTTP, mail, chat, schedule, events) and runs their ETL nodes (extract, transform,
load). The repository is MIT licensed and consists of three projects, plus the chart, the tests and the pipeline:

- `src/MeshAdapter`: the main executable service.
- `src/MeshAdapter.Sdk`: the SDK with the pipeline nodes (`Nodes/Extract|Transform|Load|Trigger`) and the services
  (`Services/`), published as a NuGet package.
- `src/MeshNodes.Sdk`: the node configuration definitions, from which the pipeline schema is generated.
- Beyond the projects: `src/charts/octo-mesh-adapter` (the Helm chart), `tests/` (unit, integration and chart tests) and
  `azure-pipelines.yml`.

## Read before you change

<!-- >>> generated: routing -->
| When you change | Read first |
|---|---|
| `src/charts/**`, `tests/chart/**`, `src/MeshAdapter.Sdk/Leasing/**`, `src/MeshAdapter.Sdk/Configuration/**` | `docs/chart-credentials-pools-publishing.md` |
| `src/MeshAdapter.Sdk/Nodes/Extract/{GetStreamData,AggregateStreamData}Node.cs`, `src/MeshAdapter.Sdk/Nodes/StreamData*.cs` | `docs/concept-getstreamdata-node.md` |
| `src/MeshAdapter.Sdk/Services/**`, `src/MeshAdapter/**` | `docs/core-services.md` |
| `src/MeshAdapter.Sdk/Nodes/Extract/**`, `src/MeshAdapter.Sdk/Nodes/MailFolders/**`, `src/MeshAdapter.Sdk/Nodes/StreamData*.cs` | `docs/nodes-extract.md` |
| `src/MeshAdapter.Sdk/Nodes/Load/**`, `src/MeshAdapter.Sdk/Nodes/Sftp*.cs` | `docs/nodes-load.md` |
| `src/MeshAdapter.Sdk/Nodes/Transform/**` | `docs/nodes-transform.md` |
| `src/MeshAdapter.Sdk/Nodes/Trigger/**`, `src/MeshAdapter.Sdk/Nodes/ConfigurationSettingsReader.cs` | `docs/nodes-triggers.md` |
| `src/MeshAdapter.Sdk/Nodes/Transform/PdfOcrExtractionNode.cs`, `src/MeshAdapter.Sdk/Nodes/Transform/OcrNativeMemoryCoordinator.cs` | `docs/pdf-ocr-extraction.md` |
| `src/MeshAdapter.Sdk/Services/PipelineIdentityResolver.cs`, `src/MeshAdapter.Sdk/Services/MeshEtlContext.cs`, `src/MeshAdapter.Sdk/IMeshEtlContext.cs` | `docs/pipeline-execution-identity.md` |
| `src/MeshAdapter.Sdk/Common/{PlatformOnlySecrets,SecretAttributes,GenericCreate*,RtAttributeAccess*}.cs` | `docs/secrets-and-write-guards.md` |
| `Directory.Build.props`, `src/MeshAdapter.Sdk/Common/JsonNullAsDefaultAttribute.cs`, `src/MeshAdapter.Sdk/Nodes/JsonDuplicateKeys.cs` | `docs/serialization-and-build.md` |
<!-- <<< end generated: routing -->

Background, no path trigger: the [developer guide](docs/developer-guide.md) (every node with its configuration, the
execution flow, HTTP handling), the [unit test concept](docs/test-concept.md), the
[integration test concept](docs/integration-test-concept.md) and the sample pipelines in `docs/examples/`.

## Build & test

```bash
# Local build against the DebugL package chain (version 999.0.0, packages from ../nuget)
dotnet build -c DebugL

# Run the adapter
dotnet run -c DebugL --project src/MeshAdapter/MeshAdapter.csproj

# All tests: unit and integration (the integration tests need Docker for the MongoDB Testcontainer)
dotnet test -c DebugL

# Unit tests only, one project
dotnet test -c DebugL tests/MeshAdapter.Sdk.Tests

# Chart tests (need helm)
bash tests/chart/test-chart.sh
```

- The version comes only from the pipeline: a Release build without `-p:OctoVersion=X.Y.Z` fails with `OCTO0001`;
  use `-c DebugL` locally ([docs/serialization-and-build.md](docs/serialization-and-build.md)).
- Nullable reference types and implicit usings are on and warnings are errors.
- The build writes `pipeline-schema.json`; `-p:GeneratePipelineSchema=false` turns that off.

## Before you commit

- `dotnet test -c DebugL` is green, unit and integration. Without Docker say so and wait for the developer's go;
  never run only the unit tests silently.
- Touched `src/charts/` or `tests/chart/`: `bash tests/chart/test-chart.sh` is green.
- Update the doc that covers what you changed, in the same commit: the matching `docs/nodes-*.md` or topic doc, and
  `docs/developer-guide.md` for a new or changed node.
- Touched `AGENTS.md` or a file in `docs/`: `Test-OctoAgentDocs -Fix` (octo-tools) reports 0 errors. It rewrites the
  generated routing table, so run it before you stage. The shim file for Claude Code is generated: edit this file, not
  the shim.

## Rules

These rules bind new and changed code. Known exceptions in existing code are named in the linked docs.

- **This repository is public.** No work item ids, internal hostnames, customer or tenant names, or secrets in docs and
  in new or changed code comments and test names (work item ids stay in `CHANGELOG.md` entries); remove an existing one
  when you touch its line.
- **Every session a node opens is classified and says why.** A new or changed node opens its repository session only
  through the identity of the pipeline (`Caller` by default; `ServiceAccount` or system by a named decision with a code
  comment), never through a parameterless overload, and is added to the classification table of
  `SessionIdentityClassificationTests` ([docs/pipeline-execution-identity.md](docs/pipeline-execution-identity.md)).
- **A node never writes the process-wide `IServiceClientAccessToken`.** It gets a service account or delegated token
  from `IServiceAccountTokenService`, which returns it, fails closed when there is none, and registers every token it
  holds with `INodeContext.RegisterSecret` before any log line or call
  ([docs/core-services.md](docs/core-services.md), [docs/nodes-transform.md](docs/nodes-transform.md)).
- **No credential in a node's output, an error message or a log line.** New or changed code never puts an SFTP, IMAP or
  HTTP credential, an AI key or a token into the data a node emits, into an exception or error text, or into a log
  property; exception text from a call that handled a credential is not passed on unredacted. The one named exception
  is a node whose purpose is to reveal a secret (`RevealSecret@1`): it writes the value only to its configured target
  path, behind the platform-only check and the allow-list below; a new node of that kind is a named decision, never a
  side effect.
- **No pipeline reveals a platform-only secret.** New code that decrypts a secret (`.Unprotect(`, `.RevealOrNull(`)
  consults `PlatformOnlySecrets` and goes on the allow-list of `SecretDecryptionCallSiteArchitectureTests`
  ([docs/secrets-and-write-guards.md](docs/secrets-and-write-guards.md)).
- **Nodes that write or read entities go through the guards.** A node that inserts runs the generic-create guard, and
  one that reads or writes attributes applies the attribute access rules (Hidden, MethodOnly, ReadOnly), wired like the
  nodes in the tables of [docs/secrets-and-write-guards.md](docs/secrets-and-write-guards.md).
- **A released node version keeps its behaviour.** A change that alters what `Name@N` does ships as `Name@N+1`; a new
  option on an existing version is optional with a neutral default and inert unless configured
  ([docs/nodes-transform.md](docs/nodes-transform.md), "MakeHttpRequestNode").
- **Pipeline data is System.Text.Json and path-only.** Nodes use `IDataContext`, no Newtonsoft and no
  `JsonSerializerOptions` on the data path. Where a value must be boxed to a CLR object, box only through
  `JsonScalar.ToClr` and keep parity with the Newtonsoft behaviour of the node's released version
  ([docs/serialization-and-build.md](docs/serialization-and-build.md)).
- **A Load node honours the dry run**: it writes nothing, reports its intent and is listed in
  `DryRunHonouredLoadNodes` ([docs/nodes-load.md](docs/nodes-load.md)).
- **A test class that reads the pipeline schema joins `PipelineSchemaCollection`.**
- **The chart gives a pool member no installation-wide credential**, and `tests/chart/test-chart.sh` pins it
  ([docs/chart-credentials-pools-publishing.md](docs/chart-credentials-pools-publishing.md)).
