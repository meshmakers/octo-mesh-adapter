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
   - **GetQueryByIdNode** — Executes a persisted query by RtId. Resolves the shared `RtPersistentQuery` base, so the caller does not need to know the kind in advance. Supports runtime-data queries (`RtSimpleRtQuery`, `RtAggregationRtQuery`, `RtGroupingAggregationRtQuery`) via `TenantRepository.GetRtEntitiesGraphByTypeAsync`, and **stream-data queries** — simple (`RtSimpleSdQuery`), aggregated (`RtAggregationSdQuery`), grouped-aggregated (`RtGroupingAggregationSdQuery`), and downsampled (`RtDownsamplingSdQuery`) — via the tenant's `IStreamDataRepository` (resolved through `ISystemContext.FindTenantContextAsync(tenantId).GetStreamDataRepository()`, same pattern as `SaveStreamDataInArchive`). Stream-data result shapes mapped into `QueryResult`: simple → **time series** with a leading `Timestamp` column then the projected attribute columns (differs from the runtime simple query, which is one row per entity with no timestamp); aggregated → single row; grouped → one row per group (group-by columns then aggregates); downsampled → **binned time series** with a leading `Timestamp` (bin start) then one column per aggregation, one row per bin (empty bins keep their timestamp and carry null aggregates). Projected values are looked up by their physical CrateDB column name — attribute path with dots stripped and lower-cased (`amount.value` → `amountvalue`); aggregate values by `{physicalColumn}_{funcToken}` (`amountvalue_sum`). Optional `From`/`To`/`Limit` config values override the time range / row cap persisted on the query, and `FromPath`/`ToPath`/`LimitPath` read those same three values from the pipeline data via JSONPath (for ranges computed upstream, e.g. an HTTP trigger) — precedence per value is literal → path → persisted. Values may be ISO-8601 strings or date/time values; a value without a time-zone offset is read as UTC — literals and path values alike, on every stream-data query kind (AB#4734) — so the adapter's local zone never shifts the window. A path resolving to nothing falls back to the persisted value with a warning, while a present but non-date (resp. non-integer) value fails the node rather than silently widening the range. `Skip`/`Take` map onto the paginated read (offset / page size) for simple queries and are ignored by the stream-data aggregation / grouped-aggregation paths. **Downsampling** (AB#4195/AB#4233) runs `ExecuteDownsamplingQueryAsync`: `From`/`To` plus a positive `Limit` — here the *bucket count*, not a row cap — are mandatory and validated on the node (so a misconfiguration is a pipeline exception, not a storage-layer error), as is a non-empty column list; `Skip`/`Take` page the bins **in memory** because the generated bin axis ignores offset/page size; and the engine clamps the bucket count down to the distinct source bins in range (AB#4246). **Resolution-aware archive selection** (AB#4290) is inherent to this query type — there is no switch: every downsampling query is routed through `SeriesResolutionService(GetArchiveRuntimeStore(), new RollupDependencyGraph(GetRollupArchiveRuntimeStore()))` — composed per tenant, exactly as the asset-repo GraphQL `streamData.resolveSeriesQuery` and the MCP `resolve_series_query` do — using the effective `Limit` as target points and the first column's attribute path as the source path. The required aggregation is never guessed: it is the first column's persisted aggregation, or the node's optional `Aggregation` override, which mirrors what a query definition can carry (Count/Minimum/Maximum/Average/Sum) and — when set — replaces the aggregation on *every* column so the values read back match the rollup that was selected. **The result never depends on which archive was read** — `From`/`To`/`Limit` are executed verbatim (the bin geometry is never fitted to the rollup), and a rollup is only accepted when it answers the query *identically* to the persisted archive, so the node and the Refinery Studio can never disagree. Accepted only when all of: the bin width `(To-From)/Limit` is a whole multiple of the rollup's `BucketSize`; `From` sits on the rollup's tick-anchored bucket grid; the alignment is `FixedSize` (calendar rungs can't line up with fixed-width bins); and `LastAggregatedBucketEnd >= To`. Otherwise the persisted archive is read and the failing condition is warned — it is nearly always a fixable query detail. Field example (AB#4725): 7 days / **10** buckets = 16 h 48 min bins over an hourly rollup drops one hour per bin (8 of 168 hours, −4.1 % total, −27 % on a single bin); 7 days / **12** buckets = 14 h bins uses the rollup and matches exactly — pick a `Limit` that divides the range into whole multiples of the bucket size. The archive actually queried is logged at info level with bin width and bucket size. Every non-`Ok` signal warns too, and the resolver's point count never overrides the query's bucket count; `EmptyLadder` / no rollup store fall back to the persisted archive. The node exposes no time-zone option, so the resolver resolves in UTC — irrelevant in practice, since the exactness check rejects calendar-aligned rungs anyway and only fixed-size ones are ever read. See Azure DevOps AB#4195, AB#4233, AB#4290.
   - **GetStreamDataNode** (`GetStreamData@1`) — Ad-hoc counterpart of `GetQueryById@1`: reads rows straight out of a stream data archive with archive, columns, time range, filters and sorting configured **on the node**, so no persisted query entity is needed. Resolves `IStreamDataRepository` and the `ArchiveSnapshot` via `ISystemContext.FindTenantContextAsync(tenantId)` → `GetStreamDataRepository()` / `GetArchiveRuntimeStore().GetAsync(archiveRtId)`; the snapshot supplies the `TargetCkTypeId` every options object needs plus `UsesWindowedStorage`. Reads via `ExecuteQueryAsync` into a `QueryResult` (leading `Timestamp` column, then the projected columns; on a windowed archive `WindowStart`/`WindowEnd` are inserted after `Timestamp` — those archives alias `window_end` as the time axis and the window columns only reach `StreamDataRow.Values` when projected explicitly). **An unset `Columns` reads the whole archive** — every column of `snapshot.Columns` the resolver accepts, preceded by a `WellKnownName` column taken straight off `StreamDataRow.RtWellKnownName` — so the minimal configuration (just an `archiveRtId`) returns usable data instead of only the time axis. Computed columns are included, addressed by their `Name`; a column mid-backfill is not, because the storage resolver hides it until the backfill commits. A *configured* `Columns` list is honoured as given, except that a name resolving to the time axis or the row window (`Timestamp`, `WindowStart`/`WindowEnd`, or their physical spellings) is dropped — those are emitted for every row anyway, so keeping it would append a second, identical column; a list that reduces to nothing stays explicit rather than becoming "read the whole archive". **Column names — projection, sorting, filtering, grouping — are resolved through the storage layer's own `StreamDataFieldResolver` rather than derived from the attribute path** (AB#4764): `ResolveQueryableColumn` returns `(QueryName, StorageKey)`, the first for the query, the second for reading the value back out of `StreamDataRow.Values`. Deriving the key (dots stripped, lower-cased) worked only by coincidence for a version-0 computed column and returned `null` for every row once a formula change moved it to `{base}__v{N}` — `ComputedColumnNaming` is internal to the CrateDB provider, so the versioned name is not reproducible. Asking the resolver also means "does this column exist" is answered by the same code the query would use, which is why the node can reject an unknown name itself — originally instead of letting the storage layer drop it silently, since AB#4765 simply earlier and with pipeline context. `GetQueryById@1` shares the resolution but treats a missing archive snapshot as non-fatal (warn, standard columns only), because its downsampling path deliberately degrades rather than failing. **`SortOrders` / `FieldFilters` name columns as they appear in the result** (`Timestamp`, `WindowStart`/`WindowEnd`, `WellKnownName`, or any archive column) and the node translates them onto the physical storage columns — `Timestamp` → `window_end` on a windowed archive, which has no `timestamp` column at all. Load-bearing: the storage layer knows only the physical names, so an untranslated `Timestamp`/`WindowStart` never reaches the column it names. The node also **rejects** a name it cannot resolve. That rejection began as a guard against the storage layer dropping such a name without raising anything (`AddSortOrders` / `BuildFieldFilterDtos` both `continue`d past it — a mistyped sort silently returned storage order, a mistyped filter silently returned too many rows); **AB#4765** fixed that in the engine (`StreamDataQueryColumnValidator` rejects for every consumer), which makes the node's translation *more* important — what was silently dropped is now a hard error — and leaves its rejection as the earlier, better-worded of two, since only the node can name the pipeline, the node and the valid names in the caller's own vocabulary. The node therefore rejects any name that is neither a result header nor an archive column and lists the valid ones; `WindowStart` on a *raw* archive is refused too, since that shape has no row window. `WellKnownNames` becomes an ordinary `rtWellKnownName` field filter (`Equals` for one value, `In` for several) — it is a standard column on every archive table; `RtIds` maps onto `WithRtIds`, `Skip`/`Take` onto offset / page size, `Limit` onto the row cap. `From`/`To`/`Limit` may also be read from the pipeline data via `FromPath`/`ToPath`/`LimitPath` (literal wins over path); values without a time-zone offset are read as **UTC** (AB#4734). Both time boundaries are independently optional. Validates on the node — inverted range, non-positive `Limit`, unparsable RtId, archive not found — so a misconfiguration is a pipeline exception, not a storage-layer error. **Gap detection** (AB#4728): setting `GapsTargetPath` additionally writes a coverage report — **per source entity** (`rtId`), so a missing quarter-hour on one meter is not hidden by another meter delivering. `StreamDataGapAnalyzer` (pure, DB-free, in `Nodes/`) clamps every `[window_start, window_end)` to the range, merges overlapping/adjacent ones and reports the uncovered remainder — **coverage/union, not a fixed grid**, so it needs no declared `Period` (a `TimeRangeArchive`'s is advisory and may be null) and handles variable window lengths; a known interval (`ExpectedInterval` → `snapshot.Period`) only adds the counts, without one the ranges are still reported and the node warns once. Runs a **second, separate query** (only `window_start` projected — `window_end` arrives as the row `Timestamp`, the name sits on the row) because the data query's `Limit`/`Skip`/`Take` would hide rows and invent gaps; capped by `MaxGapScanRows` (default 200 000), exceeding it fails rather than reporting from a truncated scan. `RtIds` and field filters are resolved **once** and shared by both queries — they read JSONPath and warn, so a second resolution would duplicate both. Requires `UsesWindowedStorage` and both boundaries; a raw archive is refused. An entity with *no* rows is invisible to a coverage scan — where `RtIds` names it, it is reported as a full-range gap instead. Overlaps are flagged (`hasOverlaps`) and warned about but never fail: legal per the storage concept, yet a sum over them double-counts. **Deliberately no downsampling and no resolution-aware rollup selection**: both live in `GetQueryById@1`, and this node always reads exactly the configured archive. See AB#4722 / AB#4726 / AB#4728.
   - **AggregateStreamDataNode** (`AggregateStreamData@1`) — Condenses archive columns into key figures over a time range (sum of a month's energy, max data quality), optionally grouped (`GroupBy: [rtId]` → one row per source entity). Sibling of `GetStreamData@1`: same archive/filter/time-range resolution via the shared `StreamDataNodeHelpers`, but `Columns`/`SortOrders`/`Skip`/`Take`/`Limit` are **absent** rather than present-and-ignored — meaningless for an aggregation. Dispatches to `ExecuteGroupedAggregationQueryAsync` when `GroupBy` is set, else `ExecuteAggregationQueryAsync`. **Functions: Count/Minimum/Maximum/Average/Sum only** — `TimeWeightedAverage`/`StateDuration` are refused because they need per-column metadata this node cannot carry (comparison value; the raw archive's LOCF path) and their result keys differ (`_twavg`, two columns on rollups); the error points at `GetQueryById@1` with a persisted per-column query. Result is a `QueryResult`: group-by columns then one column per figure, values read via `ResolveStreamAggregationValue` (`{col}_{token}` + `{Func}_{col}` fallback); the same path aggregated twice gets `{path} ({Function})` headers since the keys are function-unique but bare headers would collide; without `GroupBy` exactly one row is emitted even on an empty result, so consumers always find the expected shape. **`RequireGapFree`** runs `StreamDataGapScanner` (the coverage scan extracted from `GetStreamData@1` so both nodes share query, row cap, interval fallback and overlap warning) **before** aggregating and fails naming the short series — an incomplete month must never return a figure that looks valid but is too low. Overlaps don't fail the guard (legal per the storage concept, and the name promises gap-freedom) but are warned about, since a `SUM` double-counts them. `Average` is arithmetic, not time-weighted. `GroupBy`/`FieldFilters`/`attributePath` go through `ResolveQueryableColumn` — on an aggregation a dropped filter inflates the figure and a dropped group-by column collapses every group into one row — the storage layer rejects both itself since AB#4765, but the node's message names the pipeline. One deliberate difference remains: the engine skips a filter carrying **no comparison value** without resolving its name (a tolerated no-op for half-filled filter rows in the query editor), whereas the node validates every configured filter's name regardless — in a pipeline a filter with no value is a mistake, not an unfinished form. See AB#4722 / AB#4752.
   - **SftpListNode** (`SftpList@1`) — Lists a remote directory over SFTP and writes one element per matching file to `TargetPath`: `name`, `fullPath`, `length`, `lastWriteTimeUtc` and a `source` object naming the `serverConfiguration`, `remoteDirectory` and `filePattern` the element came from. **Metadata only** — content is read separately with `SftpDownload@1`, so a consumer can drop already-processed files before anything is transferred; listing and downloading in one node would re-fetch every kept file on every run. `FilePattern` is a glob (`*` any run, `?` one character, anchored, case insensitive, every other character literal, `SftpFileNameGlob`); `MinFileAgeSeconds` omits entries still being written. Directory entries are excluded, the result is ordered by name (ordinal), and **an empty result still writes an empty array** because a downstream `ForEach@1` aborts with `PathMustBeArray` on a missing iteration path. `lastWriteTimeUtc` is emitted with an explicit `yyyy-MM-ddTHH:mm:ss.fffffffZ` rather than the round-trip specifier, which renders according to the value's `Kind` and would give a Local value a daylight-saving-dependent offset: consumers derive a file identity from that string, so the same instant has to read identically every time. The `source` stamp exists so a consumer can scope its own bookkeeping without repeating the three connection values in its own configuration. `RemoteDirectory` and `FilePattern` both carry a runtime guard, because `required` is a C# concept the pipeline deserializer does not enforce — an omitted directory would otherwise reach SSH.NET as an empty path. The glob is compiled once per listing rather than per entry. Connection, concurrency limit and host key check come from the shared `ISftpSessionFactory`. **Caveat shared by all three SFTP nodes:** `SftpServerSettings` reads `HostKeyFingerprint`, `ConnectTimeoutSeconds`, `OperationTimeoutSeconds` and `WaitForSlotTimeoutSeconds`, but the CK type `System.Communication/SftpConfiguration` (CCS, `isFinal`) declares none of them, so they never reach the serialized entity and stay at their defaults on every tenant — effective behaviour today is trust-any host key, no per-request timeout, unbounded slot wait. The consumer side is complete and waits on a CCS CK-model change, the same route `MaxConcurrentConnections` took. `OperationTimeoutSeconds` also limits **one protocol request**, not a whole transfer: a slow-but-answering server never trips it.
   - **SftpDownloadNode** (`SftpDownload@1`) — Downloads exactly one file and writes its decoded content to `TargetPath`. Read counterpart of `SftpUpload@1`, which writes exactly one file; designed to run inside a `ForEach@1` over an `SftpList@1` result, one session per file. The remote path is static (`RemotePath`) or resolved from the data context (`RemotePathPath`, takes precedence). `Encoding` defaults to `utf-8` and is validated when the configuration is bound, so a typo fails the deployment rather than the first download; `OnEncodingError` chooses between a lossy read with a warning and failing the node (`SftpContentDecoder`, read counterpart of `SftpContentEncoder`). Single-byte code pages such as ISO-8859-1 map every byte, so the failure path is only reachable for multi-byte encodings. A leading **UTF-8 byte-order mark is stripped** from the decoded string: a BOM is valid UTF-8, so neither the strict pass nor `OnEncodingError` reports it, and kept it becomes an invisible first character that turns a downstream header comparison or split into a silent mismatch — stripped after decoding, not from the bytes, so under a single-byte code page the same three bytes stay the three ordinary characters they are there. **`MaxFileSizeBytes`** (default 100 MiB) bounds what the remote side can make the adapter allocate — the file is held in memory and then decoded to a string, so the peak is roughly 3x the file and a multi-gigabyte drop would take the pod down and repeat on every tick. Enforced twice: against the server's own `GetAttributes().Size` before a byte is transferred, and again while the bytes arrive (`OpenRead` + a hand-written copy rather than `DownloadFile`/`ReadAllBytes`), so a file that grows between the two — or a server that lies about the size — cannot get past it. Non-positive is rejected on the node; there is deliberately no unlimited setting, since the content becomes a `string`. No dry-run branch: reading has no side effects and the downstream chain must see the content in a dry run too. Wiring note: `ForEach@1` seeds the element under its `KeyPath` **default `$.key`**, so `RemotePathPath` reads `$.key.fullPath` unless the loop sets `keyPath` itself.
   - BackfillFromRtEntityNode
   - GetAssociationTargetsNode

2. **Transform Nodes** (`src/MeshAdapter.Sdk/Nodes/Transform/`): Data processing nodes
   - DataMappingNode
   - JoinNode
   - FilterLatestUpdateInfoNode
   - **MakeHttpRequestNode** (`MakeHttpRequest@1`) - Executes one HTTP request, or walks a paged endpoint, and stores the response at `TargetPath`. Four capabilities were added additively and each is **inert unless configured**, so a pipeline that sets none of them behaves byte for byte as before. **Configured access:** `ApiConfiguration` names a GlobalConfiguration entry supplying `baseUrl` and `apiKey` (`HttpApiSettings`, resolved through the same pattern as the SFTP nodes); the URL is then a path relative to that base and the key travels in `AuthHeaderName` (`Authorization` by default, scheme-less unless `AuthHeaderValuePrefix` supplies one), so the key never has to sit in the pipeline definition or in the data context. **A URL that names its own scheme is refused before any request goes out** when `ApiConfiguration` is set - the entry already decides the host, so a scheme in the URL is a contradiction the author should hear about rather than a request nobody meant. The test asks whether a scheme is spelled out (`HasExplicitScheme`), deliberately **not** `Uri.TryCreate(UriKind.Absolute)`: on Unix a leading slash makes that succeed with an implicit file scheme, so `/article` - the ordinary way to write a path - counts as absolute on a Linux agent and not on a Windows one, which is a green local suite and a red CI. **Paging:** `Paging.ItemsPath` names the array inside one response (single-level `$.name`); the walk appends the page and page-size parameters to whatever query the URL already carries and collects every page into one flat array written once, at the end, so a run that fails part way leaves no half-filled array. It stops on an empty page and, unless `StopOnShortPage` is off, on a short one; a response carrying no array at `ItemsPath` and reaching `MaxPages` both **fail** rather than truncating quietly - a target that ignores the page parameter would otherwise return the same page forever. **Retry and timeout:** `Retry` spends its attempts on the page that failed and never restarts pagination; transient means 5xx, 408, 429, network errors and timeouts, everything else fails at once. `TimeoutSeconds` applies per attempt through a `CancellationTokenSource` built on the node's `TimeProvider` - the shared `HttpClient`'s own timeout is process-wide and is never touched. **Failure semantics:** `OnHttpError` defaults to `LogAndStop`, which is what the node has always done - report and skip the following nodes while the execution still succeeds. The default deliberately stays that way: `MakeHttpRequest@1` is released and its consumers cannot be enumerated, so flipping it would turn other tenants' pipelines red on their next chart update (the house pattern for that is a new node version). `Throw` fails the execution instead, and it fails with a **typed** exception, never a raw cancellation - `ForEach@1` isolates every exception except `OperationCanceledException`, so a timeout escaping raw would abort a whole loop instead of failing one iteration; that is what makes per-item isolation with `continueOnError` work. Configuration mistakes (undefined or half-filled entry, a scheme-qualified URL with `ApiConfiguration`, paging without `ItemsPath`) **always** throw, whatever `OnHttpError` says - a mistyped entry that merely logged would leave an operator with a green execution that called nothing. Everything that is not an HTTP outcome - a response the storage step cannot handle, a header the target refuses - keeps the node's original report-and-stop net in **both** modes. **Guarded against the ways these could fail quietly:** the configured key is attached with `TryAddWithoutValidation`, because the strongly typed parsers reject perfectly good keys (a base64 key with '=' padding under the default `Authorization` header) and put the offending value into the exception message, which the node's own net would then log - so a validating add both lost the request and leaked the key. A header parameter of the same name as the auth header is refused as a configuration mistake, naming the header and never the value. The resolver checks that `baseUrl` is an absolute http/https URL, because a scheme-less base passes a blank check and only fails deep inside the send, where it is reported and swallowed. A computed backoff wait is **clamped** to 60 s while `MaxAttempts` above 10 is **rejected** - a wait can be honoured approximately, an attempt count cannot, and silently running fewer attempts than were asked for is the kind of thing an operator discovers during an incident. The same rule decides the rest of the preflight: `timeoutSeconds` must be positive and within what the timers accept (only leaving it out means "keep the client's own"), `paging.pageSize`/`maxPages` at least one and `firstPageNumber` not negative, `authHeaderName` a real HTTP token, and a URL carrying a fragment is refused while paging is on because a fragment never reaches the server and the page parameters behind it would be dropped from every request. Paging refuses `responseFormat: Base64`, `contentLengthTargetPath` and a URL that already carries one of the page parameters, each of which would otherwise be ignored or make every page identical until a page cap it never really reached. **A retry re-sends the request unchanged**, so on a non-idempotent method it can repeat a state change the target already applied - documented on the property rather than prevented, because which targets tolerate that is the pipeline author's call. The `LogAndStop` path logs the **full** response body, which is what the node reported before it could throw; only the thrown message is truncated. **Explicit nulls:** the new numbers and strings are nullable with constant defaults resolved where they are read, because the pipeline definition deserializer is YamlDotNet and a key that is present and null overwrites a property initializer (the `JsonNullAsDefaultAttribute` used by the settings records only covers the System.Text.Json path).
   - **RenderDelimitedTextNode** (`RenderDelimitedText@1`) - Renders an array of records into ONE delimited-text document at `TargetPath`: one row per array element, one column per `Columns` entry, joined with `Delimiter` and `LineEnding`. Write counterpart of `ImportFromCsv@1` - though not a round trip: that reader treats an unescaped `"` as the start of a quoted field, so a value containing one comes back split differently than it went out, and this node emits no quoting to signal it. What could not be composed from existing nodes is the last mile: `Concat@1` already builds a single delimited *line* from relative `ValuePath`s, but nothing joins an array of strings into one document. A column is a constant (`Value`), a read (`ValuePath`, relative to the record) or - with neither set - **empty**, which is how a fixed layout expresses a reserved field; such layouts are mostly reserved fields, so the empty entry is the common case rather than an oddity. Setting both is a configuration error. **`Required`** turns an empty rendered value into a failure naming record and column, checked on the rendered text so absent, null and empty are one rule instead of three; unset it is inert. **Value to text follows the house rule** the other text-producing nodes use (`Concat@1`, `FormatString@1` via `JsonStringifyHelper`, which is `internal` to the SDK and had to be reproduced rather than reused): a number emits its **raw JSON token** so `0.00` stays `0.00` and no culture can reshape it, a boolean emits `True`/`False`, absent and null emit nothing. An object or array at a `ValuePath` is **refused**, because the same house rule would serialise it as indented multi-line JSON and destroy the record structure far more quietly than a stray delimiter would. **There is no quoting**, deliberately: fixed-layout delimited formats generally have no escaping convention, so emitting quotes would hand them to the receiver as payload. A value carrying the delimiter, CR or LF is refused (`OnDelimiterInValue: Fail`, the default) or rewritten (`Replace` / `Strip`, both warned) - never passed through, since it shifts every following column and the receiving side cannot tell. Constants are checked identically, and a `Replacement` containing the delimiter or a line break is rejected in the preflight because it would only move the problem. **The delimiter is exactly one character**, which is not a simplification but the condition under which the guarantee holds at all: cleaning can compose a longer delimiter out of the characters it leaves behind (removing `ab` from `aabb` yields `ab`), and a per-value check cannot see one that forms across the join between two columns; the counterpart reader splits on the first character only, so a longer one would not round-trip either. **An empty input array writes an empty string and never skips the write** - load-bearing rather than tidy: a downstream `If@1` guarding the delivery compares against `""`, and with the path absent that comparison reads `null`, so "not empty" is TRUE and exactly the empty delivery the guard exists to stop goes out. A `Path` that is not an array fails instead, since a silently one-row document from a mis-typed path is worse than a loud error, and so does a **record that is not an object** - every read column would render empty while the constants print, which is structurally valid output with nothing in it. If the source ever reports records that the iteration does not yield, that fails too rather than writing an empty document over them. `LineEnding` is `Lf` or `CrLf` and never `Environment.NewLine`, so the same definition produces the same bytes on every host. Configuration mistakes - empty/null `Columns`, a null entry, a column setting both, an unusable `Delimiter` or `Replacement`, an undefined `LineEnding`/`OnDelimiterInValue`, a `Path`/`ValuePath` that does not parse or that selects a set (wildcard, filter, recursive descent - a column holds one value, and such a path can resolve against one backing store and render empty against another), and a blank `Path`/`TargetPath` - **always** throw before anything is written or the chain continues. The blank target path matters more than it looks: the data context reads an empty path as the document ROOT, so the rendered document would replace the entire pipeline data while the chain carried on. **Explicit nulls:** `LineEnding`, `TrailingNewLine`, `OnDelimiterInValue` and `Replacement` are nullable with constant defaults resolved where they are read, because the pipeline deserializer is YamlDotNet and a present-but-null key overwrites a property initializer (`JsonNullAsDefaultAttribute` covers only the System.Text.Json path).
   - ImportFromExcelNode
   - **ImportFromCamt053Node** (`ImportFromCamt053@1`) — parses a camt.053.001.02 XML bank statement into an array of normalized booking objects (one per `Ntry`) written to `TargetPath`. **Namespace-agnostic** (resolves by local element name), so both the ISO standard namespace (`urn:iso:std:iso:20022:tech:xsd:camt.053.001.02`) and the Austrian STUZZA/APC variant (`ISO:camt.053.001.02:APC:STUZZA:payments:003`) are handled by the same node — a namespace-bound parser silently yields zero entries on the other dialect. Reads the base64 file from `$.files[FileIndex]` (FromHttpRequest upload). Emits per entry: composite `transactionId` (IBAN|AcctSvcrRef, else IBAN|LglSeqNb|position), signed `amount` (sign from CdtDbtInd), currency, booking/value dates, `direction` (0=Credit/1=Debit), direction-dependent counterpart name/iban/bic, concatenated `purpose` (Ustrd + structured CdtrRefInf/Ref), payment/E2E/mandate references, SEPA creditorId, `bankTransactionCode` (BkTxCd SubFmlyCd — ESCT/ESDD/CWDL/STDO/…), plus accountIban/lglSeqNb/position. Feeds the standard GetOrCreate→CreateUpdateInfo→ApplyChanges flow to create Basic.Accounting/BankTransaction (MatchState=Unreviewed). Parser core `ParseCamt053` is pure/static and unit-tested; the real-corpus regression test is gated on the `CAMT_CORPUS_DIR` env var (confidential data never enters VCS). Built for the accounting BelegCockpit camt import (AB epic Rechnungseingang/Bankabgleich).
   - **AnthropicAiQueryNode** (`AnthropicAiQuery@1`) — queries Claude, optionally with MCP tools loaded from `{mcpServerUrl}/{tenantId}/mcp`. MCP auth normally uses a **service account** (`mcpServiceAccountConfigName` → `ServiceAccountTokenService.EnsureTokenAsync`), and deliberately **degrades**: a broken `ServiceAccountConfiguration` or a missing token only warns ("MCP calls will be sent unauthenticated") so the chat keeps working tool-lessly (AB#4541). **`mcpDelegateToCaller`** (AB#5031, default `false`) switches that identity: the node exchanges `IEtlContext.CallerAccessToken` for a delegated token via `AcquireDelegatedTokenAsync`, so the MCP server applies the **calling user's** roles and data permissions instead of the service account's full `octo_api` reach. 🔴 That mode is **fail-closed** — no caller token, no service-account config, or a failed acquisition all throw; neither degrade path applies, because continuing under another identity (or none) would defeat exactly the authorization the mode exists to enforce. Requires a trigger that carries a caller token (`FromHttpRequest@2`, not anonymous); channel assistants (Teams/Signal/e-mail) have no caller and must leave the flag off. Used by the accounting app's `/aiPrompt` Q&A pipeline.
   - PdfOcrExtractionNode — for PDF input, extracts the embedded text layer first (PdfPig) and falls back to raster+Tesseract OCR (IronOCR) only for scans/image PDFs or when the text layer is below `MinTextLayerChars`. The text layer is exact where OCR is lossy (separator-less codes like invoice numbers, non-German diacritics). `PreferTextLayer` (default on) is bypassed when `ExtractTables`/`ExtractBarcodes` is requested (those need the OCR path). AB#4528.
   - GenerateAndStoreReportNode
   - **RenderDataSheetPdfNode** (`RenderDataSheetPdf@1`) — renders a generic structured data sheet (title, subtitle, labelled sections, optional footer note) to a base64 PDF via QuestPDF (Community license set in the node). Domain-agnostic: the model is assembled by the pipeline. Used for the accounting BMD handover cover sheet.
   - **MergePdfNode** (`MergePdf@1`) — concatenates an ordered array of base64 PDFs into one (PdfSharp). Skips unreadable PDFs with a warning unless `FailOnInvalidPdf`. Used to prepend the cover sheet to the original document.
   - **TransformPdfNode** (`TransformPdf@1`) — assembles an output PDF from an ordered page-op list over one or more base64 source PDFs (AB#4760): each op selects a page (`sourceIndex`/`pageIndex`) and optionally rotates it in 90° steps (`rotate`, added on top of the page's existing `/Rotate`), crops it (`crop`, a [0,1]-normalized top-left rectangle expressed in the page's FINAL displayed orientation — mapped onto the page's effective visible box, i.e. an existing CropBox when present, else the MediaBox, so repeated crops nest correctly) or restores the full page (`uncrop: true`, mutually feeding a crop in the same op relative to the full page). Op order = output page order; unreferenced pages are dropped — one contract covers rotate, crop, reorder, delete, split-select and cross-source merge. base64/scratch output mirrors `MergePdf@1` (`OutputAsScratchFile`). Geometry helper `PdfCropGeometry` is pure/static and unit-tested. Server side of the accounting document page editor (`/editDocument`).
   - **RenderHtmlPdfNode** (`RenderHtmlPdf@1`) — renders an HTML (or plain-text) document to a base64 PDF via AngleSharp (parsing) + QuestPDF (layout). Browser-free and cross-platform; supports a pragmatic HTML subset (headings, paragraphs, `<br>`, bold/italic/underline, links, ordered/unordered lists, tables, blockquotes, `<pre>`, `<hr>`, inline `data:`-URI images — image dimensions read from PNG/GIF/JPEG/BMP headers). Optional `Title`/`TitlePath` heading and `IsHtml`/`IsHtmlPath` override (auto-detects markup otherwise). Used by the accounting email import to turn a forwarded mail that carries no PDF attachment into a receipt from its body.
   - **CreateZipArchiveNode** (`CreateZipArchive@1`) — bundles `{ fileName, contentBase64 }` entries into a base64 ZIP (`System.IO.Compression`); `fileName` may contain `/` for folders (e.g. AP/AR grouping). An entry may instead carry `pathSegments` (array of folder names ending with the file name; each segment sanitized for file-system use, so data-derived values such as vendor names cannot create unintended folders). Config `appendSequenceNumber` inserts a running `_001` number before each content entry's extension (unique names); config `manifestFileName` (+ `manifestFileNameColumn`, `manifestDelimiter`) writes a CSV manifest of all content entries (per-entry `manifest` object fields + final archive path) as the first archive entry — semicolon-separated, UTF-8 with BOM (BMD/Excel-friendly). An entry may set `verbatim: true` to keep its exact name (no sequence number) and stay out of the manifest — for additional index files shipped next to the content documents (e.g. the accounting handover's `lieferanten.csv` beside `belege.csv`).
   - **ApplyDataPointMappingsNode** — Evaluates `System.Communication/DataPointMapping` entities for a source entity, applies mXparser expressions, produces update items for target entities. Supports state-name filtering via `sourceStateNamePath`. See [DataPointMapping concept](../octo-communication-controller-services/docs/concepts/DataPointMapping.md).
   - **BuildMappingTargetsNode** — Resolves all active DataPointMappings into `MappingTarget` records for data acquisition. Generic for any adapter (Loxone, MQTT, OPC-UA, Modbus). Supports sub-state resolution via RecordArray lookup.
   - **ExportDataPointMappingsNode** — Serialises the tenant's DataPointMappings into a portable document keyed by NATURAL identities (configurable identity attribute per CK type, e.g. `Loxone/Control → LoxoneUuid`, plus entity name; RtIds only as same-tenant hint), so mappings survive tenant re-initialisation. Optional `excludeNameRegex` exports only the manual delta (rule-generated mappings follow the deterministic `ruleId|rtId|state` name pattern and are reproducible via the generation pipeline).
   - **ImportDataPointMappingsNode** — Resolves an export document back to entities (per endpoint: RtId → identity attribute → unique name) and emits the GenerateDataPointMappings suggestion shape plus an `enabled` field, so the SAME downstream ForEach (GetOrCreate + CreateUpdateInfo + CreateAssociationUpdate + ApplyChanges) persists imported mappings. Unresolved entries are never guessed — they land in the `statisticsTargetPath` report for manual follow-up.
   - **MapToRecordArrayNode** — Converts a JSON key/value map into a CK RecordArray. Configurable `ckRecordId`, `keyAttributeName`, `valueAttributeName`.
   - **ResolveNotificationPlaceholdersNode** (`ResolveNotificationPlaceholders@1`) — Substitutes `${...}` placeholders in a notification template's subject and body (AB#2569). One node serves every send path; before it, each of the three EnergyCommunity send pipelines carried its own generated `PlaceholderReplace@1` rule blocks plus hand-written converter nodes, so a token wired in one and not another produced different mail from the same template. The token list, the entity each reads from and the formatting (de-AT money, `dd.MM.yyyy` dates converted into Europe/Vienna, salutation and billing-type wording taken from the app's own vocabulary) live in `NotificationPlaceholderCatalog.cs`; a pipeline declares only which of Customer / Community / BillingDocument it can supply. **Empty and absent are not the same**: a present source with an empty attribute substitutes nothing and warns, an absent source fails the node — the resolver cannot distinguish the two, and blanking a billing token on a bulk path is how a payment request reaches a member with a hole in it. **A substituted value is escaped before it goes into the body** (`&<>"'` only, not `WebUtility.HtmlEncode`, whose numeric entities for non-ASCII would reach a text-only reader as `M&#252;ller`): the value comes out of a customer record while the template around it is what an operator wrote, and only the second may carry markup — `SendEMail@2` renders the body verbatim for `Html` and through Markdig for `Markdown`, and Markdig passes raw HTML through, so a company name of `<a href="http://evil">Zahlung</a>` was a working link in a mail the community appears to have sent. **The subject is never escaped** (a header arrives as text), and the decision is the mirror image of the logo's: the logo needs certainty that markup is rendered and stays silent otherwise, while encoding treats an unwired `RenderingTypePath` as "escape it" because the sender's own default is `Markdown`. A resolver *pipeline* called through `ToPipelineDataEvent@1` could not have been shared: that node requires the target in the same DataFlow and names its queues per DataFlow, and the three send paths sit in three DataFlows.
   - **UpdateRecordArrayItemNode** — Reconstruction-style update of a single item inside a CK RecordArray on a runtime entity. The node rebuilds the array from the existing items plus the patched item rather than mutating in place, which keeps it consistent with the path-only `IDataContext` write model and avoids aliased mutation across sub-contexts.

   Transform nodes that need multi-match read/write semantics consume `IDataContext.UpdateMatchesAsync(jsonPath, body)` (per-match sub-contexts, path-only). Read-only multi-match uses `IDataContext.SelectMatches(jsonPath)`, which returns an `IEnumerable<IDataContext>` of detached sub-contexts — one per match — replacing the former `EnumerateMatches` that returned raw `JsonNode?` values.

3. **Load Nodes** (`src/MeshAdapter.Sdk/Nodes/Load/`): Data persistence nodes
   - ApplyChangesNode/ApplyChangesNode2
   - SaveStreamDataInArchive
   - EMailSenderNode
   - **EMailSenderNode2** (`SendEMail@2`) — v1 carries exactly one attachment, described by four sibling properties, and gives the body no way to address it, so a billing dispatch spends its only slot on the invoice PDF and cannot also show the community logo (AB#2570). v2 replaces those four properties with an `Attachments` list whose entries may declare a `ContentId`; such an entry becomes a `LinkedResource` on the HTML `AlternateView`, which is what makes `cid:` resolve in Outlook and Gmail — an image pointed at by URL needs a bearer token the mail client does not have, and a `data:` URI is stripped by both. **An inline entry is only fetched when the rendered body actually addresses its content id**, so a pipeline may attach a logo unconditionally without every mail paying for it; the body is therefore rendered before attachments are resolved. An optional inline entry that is missing has its `<img>` stripped from the HTML rather than leaving a broken image. `FileName`/`FileNamePath` apply to file attachments only — a linked resource carries no `Content-Disposition` and is identified by its content id alone; `ContentType`/`ContentTypePath` apply to both, and reading the type from the stored binary matters because an operator can replace a PNG logo with a JPEG at any time. `BodyFormat` (Markdown / PlainText / Html) says what the body at `Path` is, and `BodyFormatPath` reads a NotificationTemplate's `RenderingType` so the template's own declaration wins — v1 ran Markdig over every body regardless, which is why a template declaring PLAIN was still sent as converted HTML. The Markdig pipeline deliberately drops `GenericAttributesExtension`: it reads a trailing `{...}` as HTML attributes, so an unfilled `${customer.iban}` reached the recipient as a bare `$` with the token's text moved onto the paragraph. `ReplyToPath`/`ReplyToAddress` cover the common case of sending from a no-reply mailbox while wanting answers elsewhere. A text/plain alternative is emitted beside the HTML (v1 sent HTML alone), with `cid:` image markup removed since no text-only reader can resolve it — for a **Markdown** body only, and HTML-decoded, so the two halves of one mail say the same thing: the resolver escapes substituted values for the markup path, and without decoding a company name of `Müller & Söhne` reached the text reader as `Müller &amp; Söhne`. A **plain** body is literal text and is passed through untouched. Streams opened for already-resolved attachments are disposed if a later one throws: they are otherwise owned by the `MailMessage`, which does not exist yet while attachments are being resolved, so an exception outside the two the loop handles (a Mongo timeout, a cancellation) stranded every stream before it.
   - SftpUploadNode
   - **DeployPipelineNode** — Deploys a specific pipeline within the same data flow via the Communication Controller REST API. Uses `ServiceAccountConfiguration` for OAuth2 authentication. Safety: cannot deploy self, must be in same data flow.
   - **TeamsBotReplyNode** (`TeamsBotReply@1`) — sends a reply into a Microsoft Teams conversation via the Bot Framework REST API (`POST {serviceUrl}/v3/conversations/{conversationId}/activities`). Bot token via client-credentials against the `botframework.com` authority; credentials read from a `MicrosoftGraphConfiguration` (its ClientId/ClientSecret double as the bot App ID/secret). Outbound counterpart of `FromTeamsBot@1`.

4. **Trigger Nodes** (`src/MeshAdapter.Sdk/Nodes/Trigger/`): Pipeline initiation nodes
   - FromHttpRequestNode/FromHttpRequestNode2 (version 1 is deprecated; version 2 rejects callers without a valid access token unless `AllowAnonymous` is set, from another tenant, and without one of `RequiredRoles` when configured). Neither version lets the caller's `Authorization`/`Proxy-Authorization`/`Cookie` headers into `input["headers"]`: that is governed by the separate `ReceivesCredentialHeaders` flag on the route, **not** by `AllowAnonymous`. Conflating the two was a defect — apps attach the operator's token per host, not per route, so an anonymous route receives tokens it never asked for, and the data root is echoed back in the response, persistable by `SetPipelineExecutionResult@1` and visible in the Studio debug panel.
     **Caller side channel (AB#5031).** `HttpRequestService` hands the trigger's execute delegate a `TriggerCallerContext(Principal, RawAccessToken)` instead of a bare `VerifiedPrincipal`; `FromHttpRequest@2` puts the raw token on `ExecutePipelineOptions.CallerAccessToken`, which `MeshContextCreatorService` forwards to `MeshEtlContext.CallerAccessToken`. That is the ONLY route the caller's credential travels: 🔴 it must never reach `input`/the data root (echoed + persistable — the `CredentialHeaders` filter is unchanged and independent), never `VerifiedPrincipal` (projected into that same data root) and never `IEtlContext.Properties` (that dictionary hangs on the `PipelineRegistration` and is shared across **all runs**, so a token left there would outlive its request). Only a `Bearer` scheme is accepted; `FromHttpRequest@1` and `FromTeamsBot@1` discard the context.
   - FromWatchRtEntityNode
   - FromExecutePipelineCommandNode
   - FromSendNotificationNode
   - FromEmailNode (IMAP folder polling via MailKit)
   - FromMicrosoftGraphNode (Teams channel polling via Microsoft Graph)
   - **FromTeamsBotNode** (`FromTeamsBot@1`) — hosts the Bot Framework messaging endpoint `POST /{tenant}/teamsBot` (via `IHttpRequestService`), parses the inbound Teams activity, downloads file attachments (1:1 `application/vnd.microsoft.teams.file.download.info` via pre-authenticated URL; channel `reference` via Microsoft Graph SharePoint share), and emits the `EmailData`/`AttachmentData` shape at `$.Emails` plus conversation routing at `$.Conversation` (serviceUrl/conversationId/activityId/from) for `TeamsBotReply@1`. Credentials read from `MicrosoftGraphConfiguration`. Inbound JWT check via `ValidateInboundToken` (default false; validates aud+exp only — NOT the signature yet, harden before public exposure). Requires `HttpRequestService` to surface request headers (`input["headers"]`) **and** is the only trigger registering with `receivesCredentialHeaders: true`, because a Bot Framework token cannot be validated by the platform gate and the node has to read the raw `Authorization` header itself.
   - **FromMicrosoftGraphEmailNode** — Polls an Office 365 mailbox FOLDER (path like `Archive/Invoices/ToDo`, '/'-separated, resolved from the mailbox root — never the inbox unless configured) via Microsoft Graph client credentials. Executes the pipeline ONCE PER MESSAGE (batch of one `EmailData`) so success maps 1:1 to the per-message action: on success the mail is moved to `moveToFolderPathOnSuccess` (leaf folder auto-created); on failure it stays in the source folder and is retried up to `maxAttemptsPerMessage` times per adapter lifetime. Only `fileAttachment` contents are downloaded (item/reference attachments skipped). Requires Graph application permission `Mail.ReadWrite`.
     **Sender authenticity — `Authentication-Results` (AB#5011).** `IncludeInternetMessageHeaders` (default off, inert when off) adds `internetMessageHeaders` to the `$select` and surfaces the headers named in `InternetMessageHeaderNames` (default `Authentication-Results`, `Authentication-Results-Original`, `ARC-Authentication-Results`, `Received-SPF`) on `EmailData.Headers`, plus the parsed SPF/DKIM/DMARC/compauth verdicts on `EmailData.Authentication`. The header was not empty before — Graph returns `internetMessageHeaders` **only** when it is selected explicitly, so it was never fetched; that one word in the `$select` is the whole feature. It stays opt-in because selecting it drags the full Received chain and the DKIM signatures onto every message, and only the named headers are surfaced because they land in the data context (echoed into every debug view, persisted by `SetPipelineExecutionResult@1`). 🔴 **Gate on DMARC, not on SPF.** SPF authenticates the envelope sender and DKIM the signing domain; neither has to match the `From:` the pipeline reads. Only `dmarc=pass` requires that alignment, so `spf=pass` alone accepts a mail whose envelope sender is the attacker's own (perfectly SPF-valid) domain while `From:` claims the vendor's — `EmailAuthenticationResults.IsDmarcPass` is the one verdict a rule may be built on. 🔴 **Only the FIRST occurrence of each header is kept.** A sender can put an `Authentication-Results` header into the message they submit and the receiving server *prepends* its own rather than replacing it, so only the topmost was written by infrastructure we trust; joining the occurrences would put a forged `dmarc=pass` into the same string as the real `dmarc=fail`, where any downstream substring or `MatchRegEx` check finds it. The occurrence count is reported as `HeaderCount` and anything above 1 makes `IsDmarcPass` false. A **null** `Authentication` means *nothing is known* (an internally generated mail carries no such header), never "authentication failed" — which way an unknown verdict falls is tenant policy, not the trigger's call. Result keywords are lower-cased because `MatchRegEx` is case sensitive; an absent method stays `null` rather than becoming `none`, so "never checked" stays distinguishable from "checked, no policy". The parser (`AuthenticationResultsParser`, RFC 8601) is pure/static and strips RFC 5322 comments from the **whole** value before splitting — Exchange writes `(client-ip=1.2.3.4; helo=mail.example)`, and splitting first tears the entry in two and reads the comment's own `key=value` pairs as method results.

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
- **ServiceAccountTokenService**: Acquires OAuth2 tokens from `ServiceAccountConfiguration` entities for service-to-service REST calls (used by `DeployPipelineNode`).
  `EnsureTokenAsync` runs the **client-credentials** grant and writes the result into the process-wide
  `IServiceClientAccessToken` — that instance *is* the adapter's service identity (it doubles as
  `ICommunicationServiceClientAccessToken` towards the communication controller).
  `AcquireDelegatedTokenAsync` (AB#5031) runs the OctoMesh **delegation grant**
  (`grant_type=urn:meshmakers:params:oauth:grant-type:on-behalf-of`, AB#5026 in
  `octo-identity-services`): the service account authenticates with its own credentials and presents
  the end user's token as `subject_token`, so the issued token runs on the **user's** `sub` with the
  intersection of both parties' roles. 🔴 It **returns** that token instead of storing it — writing a
  user-bound token into the process-wide singleton would leak one caller's identity into every
  concurrent request. Same-tenant only, so the configuration's `TenantId` is required
  (`acr_values=tenant:X`); `offline_access` is never requested (the identity service rejects it —
  a refresh token would freeze the role intersection). Delegated tokens are deliberately **not
  cached**: any cache would have to be keyed by subject, and the only key material is the caller's
  own token.
- **AdapterEventService**: Writes `System.Notification/Event` entries tagged with the `MeshAdapter` source into the tenant's event log, the audit trail Studio shows under Repository → Events. Used to record authorization decisions on secured trigger routes; failures degrade to a log warning so auditing can never fail a request

## Pipeline execution identity — every session is classified (AB#5028)

Pipeline execution runs under a real identity instead of anonymous, parameterless system sessions.
The mechanism is a single resolution point, two methods on the context, and a classification that is
written down at every call site.

### Resolution — once per execution, lazily

`MeshContextCreatorService.CreateEtlContext` is the one point every execution flows through, so it
builds one `PipelineIdentityResolver` per execution and hands it to `MeshEtlContext`. Precedence:

1. **`ExecutePipelineOptions.VerifiedPrincipal`** (AB#4975) → `RtSecurityContext.ForUser(sub, roles)`.
   Free: it is already on the options.
2. **The adapter's / pipeline's service account** (AB#5027). The communication controller projects
   the `ServiceAccountConfiguration` into the pipeline's configuration list, so the credentials come
   out of `IGlobalConfiguration.GetAllRawJsonByCkTypeId("System.Communication/ServiceAccountConfiguration")`
   with no repository read. The **roles** are the expensive half — they are not on the entity, only
   as `role` claims on the issued token — so `IServiceAccountTokenService.AcquireServiceAccountIdentityAsync`
   requests a client-credentials token, parses it locally (`JwtPayloadReader`, no signature check:
   the token is the answer to our own request over TLS and never passed through a caller) and caches
   the result per `(TenantId, ClientId)` until shortly before the token's own `exp`.
3. Otherwise `RtSecurityContext.System`.

Resolution is **lazy and memoised**: many executions never open a session at all (high-frequency
event triggers), and they must not pay a token round trip. The identity cache is deliberately NOT the
`_tokenExpiresAt` field `EnsureTokenAsync` uses — that one is not keyed by configuration and belongs
to the adapter's own service identity, so sharing it would let one path suppress the other's refresh.

🔴 **Fail-closed once an account is configured.** A failed acquisition throws
(`MeshAdapterPipelineExecutionException.ServiceAccountIdentityUnavailable`) instead of falling back to
the system context. The system context bypasses data-level permissions entirely (AB#4969), so a
fallback would fail *open*: an identity-service outage would silently widen every read and leave every
write unstamped, indistinguishable from a correctly restricted run. The System path survives only
where **nothing** is configured — the pre-AB#5027 fleet and every tenant until provisioning has run —
because changing behaviour there would take the whole fleet down.

⚠️ **Two caveats.** `GetAllRawJsonByCkTypeId` matches `ConfigurationTypeId.SemanticVersionedFullName`,
which appends `-N` as soon as the CK **type** version passes 1: a type bump makes the match go quiet
and every pipeline fall back to the system context, and that failure looks like "nothing happened",
not like an error — a bump has to be paired with a change to
`PipelineIdentityResolver.ServiceAccountConfigurationCkTypeId`. And the adapter caches
`GlobalConfiguration` at pipeline **registration**, so changing the linked service account only takes
effect after the pipeline / data flow is redeployed.

### Distribution — `IMeshEtlContext`

| Method | Meaning |
|---|---|
| `GetScopedSessionAsync()` / `GetScopedSession()` | The effective identity. Stamps `RtCreatedBy`, subject to data permissions. |
| `GetSystemSessionAsync()` / `GetSystemSession()` | **Explicitly** `RtSecurityContext.System`. |

The second is the more important one. Its existence is what turns "which identity does this node use"
from an accident of who last touched the call site into a decision written down in the code: a node
either says scoped or it says system, and a new node has to choose. **No node calls
`TenantRepository.GetSessionAsync()` any more** — `TenantRepositorySecurityExtensions` degrades into
that overload *silently* for a repository without `ISecureSessionFactory`, which is exactly the trap
this closes. The only remaining parameterless system session in the SDK is
`ServiceAccountTokenService.ReadConfigurationAsync`, which is circular by nature: it is the read that
*answers* the identity question.

### Classification (32 call sites: 15 scoped, 17 system)

**System by decision** — each carries a code comment saying what breaks if it were scoped:

| Node | Why system |
|---|---|
| `ImportFromExcel@1` (sync) + its `WellKnownNameLoader` (sync) | An import is a bulk load belonging to the tenant; a creator stamp makes every imported row invisible to an OwnedOnly reader, and a filtered name lookup re-creates existing entities as duplicates. |
| `ImportDataPointMappings@1` / `ExportDataPointMappings@1` | Backup/restore pair. A read filter writes a *shorter* export file that looks complete, and the loss only surfaces on restore. |
| `DeployPipeline@1` | Reads pure platform types and calls the controller as the adapter's service identity — a service-identity node by construction. |
| `GetNotificationTemplate@1` | Platform configuration; a filter turns "may not see" into the same hard `TemplateNotFound` as "does not exist". |
| `CheckDuplicate@1` | Must see other people's documents, or it reports "no duplicate" and the record is created twice — silently, in exactly the case the node exists to prevent. |
| `BackfillFromRtEntity@1` | Would not find the entity and backfill nothing, with a green execution. |
| `SaveTimeRangeStreamDataInArchive@1` | The read is an orphan guard; a filter turns it into a hard "refusing to insert". |
| `GetFileSystemContent@1`, `SendEMail@1`, `SftpUpload@1`, `ToDiscord@1` (×2) | Binary download for outgoing channels: attachments regularly belong to somebody else, and a filter does not fail the node — the message goes out without its attachment. |
| `ApplyChanges@1` | Frozen: the deprecated twin of `@2`. A pipeline still on `@1` must not start stamping or filtering because the adapter was upgraded. Migrate to `@2` to get an identity. |
| `CreateZipArchive@1` / `CreateFileSystemUpdate@1` — their `GetFolderRootAsync` helpers | `System.Reporting/FolderRoot` is platform configuration; a filtered root reads as missing and the artefact is never written at all. |

**Two identities in one node:** `CreateZipArchive@1` and `CreateFileSystemUpdate@1` write real user
artefacts (scoped, stamped) but resolve their FolderRoot as system. Keep the split.

**Everything else is scoped**, including `ApplyChanges@2` (its AB#4975 branch no longer falls back to
a system session when there is no verified caller — the fallback is now the service account) and
`UpdateRtEntityIfNewer@1`. Two of them carry a warning in the comment: `ValidateDataPointCoverage@1`
produces **false positives** ("coverage missing") under a narrow identity, and
`GenerateDataPointMappings@1` can propose duplicates of mappings it cannot see.

**Not touched:** the session-less CrateDB / stream-data paths (`SaveStreamDataInArchive@1`,
`GetStreamData@1`, `AggregateStreamData@1` — they go through `IStreamDataRepository`, which opens its
own sessions internally) and `AdapterEventService`. No session is in play there.

### The test guard

`SessionNodeTestBase` (unit tests) is the reason this stays true. It fakes the repository as
`A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>())` — without that face the
security-context extension falls back to the parameterless system session **in silence**, which is
why the caller-scoped branch AB#4975 added to `ApplyChanges@2` was green for months without ever
enforcing anything. Two guards are armed: the parameterless overloads throw, and a system session
throws until a test declares `GivenSystemSessionIsExpected()`. After every test, any caller-scoped
session is checked to have carried the full identity (subject *and* roles) — a `ForUser(null, [])`
context is not the system context and would otherwise sail through.

`PipelineIdentityMatrixTests` is its sibling for the entry points — see the AB#5029 section below.

`SessionIdentityClassificationTests` scans `src/MeshAdapter.Sdk/Nodes` (located via
`[CallerFilePath]`) and pins the table above file by file, that no node reaches the repository
directly, that every call site carries its `AB#5028` reasoning, and that exactly the two known
synchronous sites are synchronous. `SessionIdentityBehaviourTests` drives the nodes that had no suite
of their own. `SessionIdentityIntegrationTests` verifies the same contract against the **real**
`TenantRepository`, where `session.GetSecurityContext()` is the truth.

### The identity ends at a pipeline chain — by decision, and visibly (AB#5045)

`ToPipelineDataEvent@1` → `FromPipelineDataEvent@1` (both in `octo-communication-sdk`) crosses the
message bus, and the trigger on the far side builds its `ExecutePipelineOptions` **without** a
`VerifiedPrincipal` and **without** a caller token. So an HTTP-triggered pipeline that chains to a
second one runs the first half as the user and the second half as the service account.

🔴 **That is the decision, not a gap.** Forwarding the identity would let a pipeline act as a caller
the *target* never authenticated: the sender picks the routing key, so whoever may enqueue into the
data flow would inherit whoever last triggered the sending pipeline — and on the fire-and-forget path
the message has no bounded lifetime, so the identity would stay usable for as long as it sits in the
queue. A privilege escalation is not something to introduce as a side effect of a chaining node. If a
chained execution should ever run as the user, the identity has to be **established** on the far side
(verified), never relayed.

What the decision costs is that one logical request runs under two identities, so the transition is
made visible instead of silent: `ToPipelineDataEvent@1` records the hand-off on the **execution log**
(`INodeContext.Info`, the channel the adapter and the Studio debug panel already surface) naming the
subject whose identity ends there and the target pipeline that will resolve its own. Deliberately not
on the message — its payload is pipeline data, and no credential may travel on it — and deliberately
not a new audit channel. Without a caller identity the same site logs at debug level: the overwhelming
majority of chains are service-to-service and an info line for each would drown the case that matters.

`FromPipelineDataEventNodeTests` and `FromExecutePipelineCommandNodeTests` (in the SDK repo) pin that
the second execution really starts with neither value, so a well-meant "the identity should survive
the chain" change fails a test rather than shipping.

### The delegation matrix — one place where the rules are proven together (AB#5029)

`PipelineIdentityMatrixTests` joins what the individual suites cover into the statement the platform
actually makes, across every trigger kind (HTTP with a verified caller, HTTP anonymous,
cron/`FromPipelineTriggerEvent@1`, `FromPipelineDataEvent@1`, `FromExecutePipelineCommand@1`, and the
channel triggers) and every identity situation:

| Rule | Where it is pinned |
|---|---|
| Precedence: verified caller ▶ service account ▶ system | `PipelineIdentityMatrixTests` (per trigger kind), `PipelineIdentityResolverTests` |
| Intersection is over **role names**; the **subject is the caller**, so owner-scoped checks (`RtCreatedBy`, `ownerAttributePath` — AB#4978) are about the human | `ServiceAccountTokenServiceTests.AcquireDelegatedTokenAsync_TheSubjectStaysTheCaller…`, `SessionIdentityIntegrationTests.WithBothACallerAndAServiceAccount_TheSessionActsAsTheCaller` |
| 🔴 An **empty intersection is fail-closed and identity-side a SUCCESS** — a valid token that simply carries no roles, whose only symptom is that nothing comes back | `ServiceAccountTokenServiceTests.AcquireDelegatedTokenAsync_AnEmptyRoleIntersectionIsASuccess…`, `PipelineIdentityMatrixTests.AnEmptyRoleIntersectionResolvesQuietlyToAnIdentityWithNoRoles` |
| A caller **with** an identity but **no roles** sees nothing on a protected type — `ForUser(sub, [])` is not the system context | `SessionIdentityIntegrationTests.ACallerWithoutRolesProducesANonSystemSessionWithNoRoles` |
| **No service account configured** ⇒ the System path, unchanged (the fleet before provisioning) | `PipelineIdentityMatrixTests.WithoutAServiceAccount_ATriggerWithoutACallerKeepsTheSystemPath` |
| **Configured account whose token cannot be had** ⇒ abort, never a System fallback | `PipelineIdentityMatrixTests`, `SessionIdentityIntegrationTests.AConfiguredServiceAccountWhoseTokenIsUnavailableOpensNoSessionAtAll` |

Two of these deserve their own warning. The **empty intersection** looks like a bug from the outside —
the assistant answers "I found nothing" — and the obvious repair is to treat a role-less delegated
token as a failed acquisition. That must never happen: `AnthropicAiQuery@1` turns a null token into a
hard failure, so rejecting a role-less one would turn a correctly restricted answer into an outage,
and the pressure to relax it back towards the service account's own reach is exactly how a delegation
feature loses its point. And **no caller must ever fall back to the service account because the caller
has no roles** — that would hand a role-less user the account's full reach.

`PipelineIdentityMatrixTests` also scans `src/MeshAdapter.Sdk/Nodes/Trigger` and pins that
**`FromHttpRequestNode2.cs` is the only trigger that sets `VerifiedPrincipal` / `CallerAccessToken`**,
plus the list of triggers that start an execution at all — the same house pattern
`SessionIdentityClassificationTests` uses for the session call sites. A trigger that starts forwarding
a principal changes the identity every pipeline behind it runs as, and does so invisibly: nothing
fails, the execution just sees different data.

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
- **GlobalConfiguration settings records are a separate path with a separate trap.** `IGlobalConfiguration.GetValue<T>` deserializes the stored payload with options it builds itself (camelCase, case-insensitive) — a converter registered anywhere else never reaches it, so the settings type is the only lever. That payload is the serialized CK entity: every attribute the CK type declares is a **present key**, and an optional attribute nobody filled in carries **null**, not a missing key. A non-nullable value-type property keeps its C# initializer only when the key is *absent*, so an unset optional CK Int fails the pipeline with `The JSON value could not be converted to System.Int32` before any node does any work — as `SftpUpload@1` did in a tenant whose `MaxConcurrentConnections` was never set. Number properties on such records therefore carry `[JsonNullAsDefault(<same value as the initializer>)]` (`Common/JsonNullAsDefaultAttribute.cs`), which makes "key absent" and "key null" both mean *not configured*; every other token keeps failing as before. Annotate every number, not just the attributes that are optional today — the crash returns the moment a CK type declares one of the others optional. Post-deserialization validation (`SftpServerSettingsResolver`'s `<= 0` check) cannot help here: it runs after the throw.

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

## The adapter's own credential in the chart (AB#5072)

`src/charts/octo-mesh-adapter` carries the three env vars the SDK's
`AdapterAccessTokenService` needs to log the adapter in **before** it connects to
`/{tenantId}/adapterHub`. All three are optional and inert when unset — an unconfigured adapter
acquires no token and connects anonymously, which is what the whole fleet does today.

| Env var | Chart value | Notes |
|---|---|---|
| `OCTO_ADAPTER__ISSUERURI` | `.Values.authUri` | **Same value as `OCTO_ADAPTER__AUTHORITYURL`, by decision.** |
| `OCTO_ADAPTER__CLIENTID` | `.Values.serviceAccountClientId` | Non-secret. Written by the communication controller as a `ValueOverride` at deploy time. |
| `OCTO_ADAPTER__CLIENTSECRET` | `.Values.secrets.serviceAccountClientSecret` via `octo-mesh.secretEnv` | 🔴 Secret-flagged; accepts a plaintext string **or** the `{valueFrom: {secretKeyRef: …}}` map the operator produces from `{release}-octo-secrets`, exactly like `secrets.rabbitmq`. |

🔴 **`AUTHORITYURL` and `ISSUERURI` are two keys for two directions, fed from one value.**
`AuthorityUrl` (`MeshAdapterConfiguration`, this repo) is **inbound** — the issuer secured
`FromHttpRequest@2` routes accept on tokens presented *to* the adapter. `IssuerUri`
(`AdapterOptions`, octo-communication-sdk) is **outbound** — the identity service the adapter
authenticates *itself* against. Two config keys exist because `AdapterOptions` lives in the SDK and
must also serve adapters with no `MeshAdapterConfiguration` (Loxone, Modbus, Zenon, the simulation
plug). They always name the same identity service, so the chart feeds both from `authUri`; a second
chart value could only ever drift. It must be the **public** issuer address — OIDC discovery runs
against it and the communication controller validates the issuer of the resulting token.

⚠️ **`octo-mesh.secretEnv` fails on an empty value**, which is deliberate for the four mandatory
cluster secrets. The client secret is optional, so its `include` sits behind an `if`; dropping that
guard makes every adapter without credentials fail to render.

The controller side of the wire (which `ValueOverride` paths are projected, why they are not gated on
`ReceivesClusterSecrets`, and why provisioning had to move before the deploy notification) is
documented in `octo-communication-controller-services/CLAUDE.md` → "Phase 4 — the credentials reach
the adapter pod (AB#5072)".

## Helm chart publishing (AB#4948)

`src/charts/octo-mesh-adapter` is packaged on every build and published to two
places, plus a third for long-lived branch lines:

| Branch | Channel | Chart version |
|---|---|---|
| `main` | dev bucket (`meshmakers.github.io/helm-chart-build/`) | `0.1.<yyMMDDxxx>` |
| `r*` tag | release (`meshmakers.github.io/charts/`) | `<MAJOR>.<MINOR>.<PATCH>` |
| `test/*` | **same** dev bucket | `0.2.<yyMMDDxxx>-test-0-2-dev` — a SemVer **prerelease** |

The test line has to publish at all because the communication operator resolves
a workload's chart from a repo URL at deploy time — unlike the core services,
whose charts the deploy lane packages from a local checkout, there is no
pipeline in that path.

It shares the dev bucket with main, which is only safe because of the
prerelease tag: a workload with an **empty** `ChartVersion` means "newest in the
repo", and that is exactly what the `System.Communication.MainLatest` blueprint
seeds on every dev/test tenant. A stable `0.2.x` chart would out-sort every
`0.1.x` and move the whole dev/test fleet onto branch charts. Helm ignores
prereleases unless the caller pins one, so an unpinned resolve keeps returning
main's newest stable chart — and that holds even when the prerelease carries the
higher number. A 0.2 instance therefore pins `ChartVersion` explicitly
(runtime-state, so the pin survives a blueprint re-apply).

The version itself is derived centrally by
`ci-templates/derive-chart-version.yml` in `meshmakers/helm-chart-build`; the
prerelease tag is derived from the branch so two test lines can coexist in one
index.

## Development Notes

- All projects have nullable reference types enabled
- Warnings are treated as errors
- Implicit usings are enabled
- The solution follows a node-based pipeline architecture where each node has a configuration class in MeshNodes.Sdk and an implementation in MeshAdapter.Sdk