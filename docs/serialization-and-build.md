---
description: System.Text.Json data path, configuration, key dependencies and pipeline schema generation.
applies_to: Directory.Build.props, src/MeshAdapter.Sdk/Common/JsonNullAsDefaultAttribute.cs, src/MeshAdapter.Sdk/Nodes/JsonDuplicateKeys.cs
---

# Serialization and build

### JSON / Serialization (System.Text.Json)

The adapter and all ~35 nodes are System.Text.Json-only on the pipeline data path. Newtonsoft is no longer used for pipeline data flow (it may still appear in unrelated transports such as SignalR contracts).

- **`SystemTextJsonOptions.Default`** (from `octo-sdk`, `src/Sdk.Common/EtlDataPipeline/SystemTextJsonOptions.cs`) — central `JsonSerializerOptions` carrying the STJ converters required by OctoMesh runtime types. The mesh-adapter no longer maintains its own bundle; all nodes that need to round-trip runtime entities, mutation DTOs, etc. reuse this single options instance from the SDK.
- **Newtonsoft-parity contract.** The numeric/scalar round-trip rules (`int` preference, `.0` emission for integral doubles/floats/decimals, `JsonScalar.ToClr` boxing) are enforced by `Sdk.Common.PipelineParityTests` in octo-sdk — Newtonsoft is the oracle. If a node consumer pattern-matches on `long` for an attribute value (e.g. `MinMaxNode`'s comparable-value switch), it must also handle `int`; values that fit in Int32 stay Int32 after the round-trip. See `octo-construction-kit-engine/docs/ck-schema-and-json-serialization.md` for the full serialization rules.
- The pipeline data context is the path-only `IDataContext` from `octo-sdk` — see the spec at `octo-sdk/docs/superpowers/specs/2026-05-06-newtonsoft-to-stj-pipeline-migration-design.md` §5. Nodes do not see `JToken`/`JObject`/`JArray` on the data flow surface; they operate via:
  - `Get<T>(path)` / `GetValue(path)` / `TryGet<T>(path, out value)` — typed scalar reads
  - `Set<T>(path, value, ...)` — typed writes; report builders use `Set<T>` with typed records instead of constructing `JsonObject` manually
  - `WriteJsonTo(path, stream)` — serialize a subtree to a stream (used for hashing, e.g. `CheckDuplicateNode` / `ApplyDataPointMappingsNode`); its `DataContextImpl` impl routes the `Utf8JsonWriter` through `SystemTextJsonOptions.Default.Encoder` (`UnsafeRelaxedJsonEscaping`) so the bytes match Newtonsoft on non-ASCII/HTML — load-bearing precisely because these consumers **hash** the output
  - `Iterate*Async(path, body)` — iteration over arrays
  - `UpdateMatchesAsync(jsonPath, body)` — multi-match read/write (per-match sub-contexts)
  - `SelectMatches(jsonPath)` — read-only multi-match; returns `IEnumerable<IDataContext>` of detached sub-contexts, one per JSONPath match (replaces the removed `EnumerateMatches` which returned raw `JsonNode?` values)
- `JsonSerializerOptions` may only appear in nodes for non-data-flow purposes (e.g. HTTP API calls, prompt serialization). Node-author code must not pass `JsonSerializerOptions` to any `IDataContext` method — all STJ details are internal to the context implementation.
- **GlobalConfiguration settings records are a separate path with a separate trap.** `IGlobalConfiguration.GetValue<T>` deserializes the stored payload with options it builds itself (camelCase, case-insensitive) — a converter registered anywhere else never reaches it, so the settings type is the only lever. That payload is the serialized CK entity: every attribute the CK type declares is a **present key**, and an optional attribute nobody filled in carries **null**, not a missing key. A non-nullable value-type property keeps its C# initializer only when the key is *absent*, so an unset optional CK Int fails the pipeline with `The JSON value could not be converted to System.Int32` before any node does any work — as `SftpUpload@1` did in a tenant whose `MaxConcurrentConnections` was never set. Number properties on such records therefore carry `[JsonNullAsDefault(<same value as the initializer>)]` (`Common/JsonNullAsDefaultAttribute.cs`), which makes "key absent" and "key null" both mean *not configured*; every other token keeps failing as before. Annotate every number, not just the attributes that are optional today — the crash returns the moment a CK type declares one of the others optional. Post-deserialization validation (`SftpServerSettingsResolver`'s `<= 0` check) cannot help here: it runs after the throw.

### Configuration

The solution uses:
- **Directory.Build.props**: Central MSBuild configuration
- Three build configurations: Debug, Release, DebugL (for local development)
- Target framework: .NET 10.0
- OctoVersion: comes only from the pipeline: `update-build-number.yml` sets `0.1.*` on main and the exact tag version on r-tags; the release trains pass `octoCoreLibVersion` / `octoCommVersion`. DebugL pins `999.0.0`, a private-feed build defaults to `0.1.*`. There is no release-line fallback in Directory.Build.props: `dotnet build -c Release` without `-p:OctoVersion=X.Y.Z` fails fast with `OCTO0001`.

### Key Dependencies

- Meshmakers.Octo.Sdk.* packages (various SDK components)
- IronOCR for PDF text extraction
- AngleSharp for HTML parsing (`RenderHtmlPdf`); QuestPDF + PdfSharp for PDF generation/merge
- Microsoft.Extensions.AI.Abstractions for the chat-client seam of the LLM query engine (abstractions only, no provider SDK)
- MongoDB for data persistence
- SignalR for real-time communication

## Pipeline Schema Generation

The build automatically generates a `pipeline-schema.json` file in the build output directory. This JSON Schema describes all available pipeline node configurations and can be used for editor autocompletion and validation.

- **Output**: `pipeline-schema.json` in the build output directory
- **Trigger**: The `GeneratePipelineSchema` MSBuild target runs after Build via `dotnet exec "$(TargetPath)" --generate-pipeline-schema <output-path>`
- **Incremental**: Only regenerates when the binary changes
- **Opt-out**: Set MSBuild property `GeneratePipelineSchema=false` to disable
- **Tests**: A test class that reads the schema joins `[Collection(PipelineSchemaCollection.Name)]` and takes `PipelineSchemaFixture` in its constructor (`tests/MeshAdapter.Sdk.Tests/Helpers/PipelineSchemaFixture.cs`), which generates the schema once per test run. The collection runs without parallelism because building the schema on two threads at once can spin forever in the process-wide XML documentation cache of Namotion.Reflection; `PipelineSchemaFixtureArchitectureTests` fails for any other test file that uses `IPipelineSchemaGenerator`, `INodeSchemaRegistry` or an SDK type that reaches the schema build on its own (`AdapterPoolMemberService`, `AdapterExecutionService`, `AdapterNodeDescriptorProjection`, `AdapterBuilder`, `WebAdapterBuilder`). Other test files may name `PipelineSchemaFixture` only in a declaration without an initializer, such as a constructor parameter or a field; every other mention (`new`, a class or collection fixture, an alias) fails that test as well. The rule is a tripwire for realistic mistakes, not a C# parser; the comment at the top of that test lists the forms it refuses on the safe side and the ones it does not cover. `PipelineSchemaCollectionTests` checks that the collection definition in effect runs without parallelism.
