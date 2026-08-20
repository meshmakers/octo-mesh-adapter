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

Executes a persisted query entity (`RtPersistentQuery`) by its RtId and writes a `QueryResult`
(`Columns` + `Rows`) to `TargetPath`. The concrete query kind is resolved from the loaded entity, so
the caller does not need to know it in advance.

| Parameter | Type | Description |
|-----------|------|-------------|
| `QueryRtId` | OctoObjectId | RtId of the persisted query entity |
| `Skip` / `Take` | int? | Paging. Runtime queries: DB paging for simple, in-memory for grouped, ignored for aggregation. Stream-data: offset / page size for simple, in-memory over the bins for downsampling, ignored for aggregation and grouped aggregation (a single row resp. one row per group) |
| `FieldFilters` | collection | Additional field filters AND-combined with the query's persisted filters |
| `From` / `To` | DateTime? | Stream-data only: override the persisted time range |
| `Limit` | int? | Stream-data only: override the persisted row cap — for a downsampling query the bucket count, which doubles as the archive selection's target point count |
| `FromPath` / `ToPath` / `LimitPath` | string | Stream-data only: read the same three values from the pipeline data via JSONPath |
| `Aggregation` | AggregationTypesDto? | Downsampling only: override the aggregation persisted on every column of the query (Count / Minimum / Maximum / Average / Sum) |

**Time range from pipeline data.** `From` / `To` / `Limit` can alternatively be read from the data
context with `FromPath` / `ToPath` / `LimitPath`, for ranges computed upstream (HTTP trigger,
preceding node) instead of configured on the node. Precedence per value: literal (`From`) →
path (`FromPath`) → value persisted on the query entity. Timestamps may be ISO-8601 strings or
date/time values; a value without a time-zone offset (`"2026-06-01T00:00:00"`) is read as UTC — for a
literal `From` / `To` as well as for a path value, on every stream-data query kind — so the adapter's
local time zone never shifts the queried window. A path that resolves to nothing falls back to the
persisted value and logs a warning; a value that is present but not a date/time (or not an integer for
`LimitPath`) fails the node instead of silently widening the range. For a multi-match JSONPath the
first match is used.

**Supported query types:**

| Query entity | Path | Result shape |
|--------------|------|--------------|
| `RtSimpleRtQuery` | runtime graph query | one row per entity (RtId, CkTypeId, projected columns) |
| `RtAggregationRtQuery` | runtime graph query | single row of aggregate values |
| `RtGroupingAggregationRtQuery` | runtime graph query | one row per group (group keys + aggregates) |
| `RtSimpleSdQuery` | stream-data repository | **time series**: leading `Timestamp` column + projected columns, one row per data point |
| `RtAggregationSdQuery` | stream-data repository | single row of aggregate values (RtId null) |
| `RtGroupingAggregationSdQuery` | stream-data repository | one row per group (group-by columns + aggregates, RtId null) |
| `RtDownsamplingSdQuery` | stream-data repository | **binned time series**: leading `Timestamp` (bin start) + one column per aggregation, one row per bin (empty bins carry null aggregates) |

Stream-data queries are executed against the tenant's `IStreamDataRepository` (obtained via
`ISystemContext.FindTenantContextAsync(...).GetStreamDataRepository()`), reading the `CkArchive`
referenced by the query's `ArchiveRtId`. Projected values are keyed in the engine result by their
physical CrateDB column name (attribute path with dots stripped, lower-cased — e.g. `amount.value`
→ `amountvalue`); aggregate values by `{physicalColumn}_{funcToken}` (e.g. `amountvalue_sum`). The
node resolves both forms so the `QueryResult` headers keep the caller's original attribute paths.
Errors surface through the standard pipeline-exception channel (query not found, missing
`ArchiveRtId`, stream data not enabled, execution failure; for a downsampling query additionally: no
aggregation columns, an incomplete or inverted time range, a non-positive bucket count, an
unsupported `Aggregation` override, and a failure inside the archive selection) — no silent empty
results.

**Downsampling** (`RtDownsamplingSdQuery`, AB#4195 / AB#4233). Executed via
`IStreamDataRepository.ExecuteDownsamplingQueryAsync`: the window is cut into `Limit` DATE_BIN
buckets and each persisted column is aggregated per bucket. `From`, `To` and a positive `Limit`
(the *bucket count*, not a row cap) are mandatory for this query type — resolved through the same
literal → path → persisted chain as every other stream-data query, and validated on the node so a
misconfiguration surfaces as a pipeline exception rather than a storage-layer error. A query without
aggregation columns is rejected as well. `Skip` / `Take` page the returned bins **in memory** — the
bin axis is generated, not paged, so the storage layer ignores offset / page size on this path. The
engine additionally clamps the requested bucket count down to the number of distinct source bins in
range (AB#4246), so a sparsely-populated window can return fewer bins than requested.

**Resolution-aware archive selection** (AB#4290). Inherent to a downsampling query — there is no
switch to turn it on or off, because choosing the archive is part of answering the query. Instead of
reading the archive persisted on the query, the node asks `SeriesResolutionService` — composed per
tenant from `GetArchiveRuntimeStore()` and a `RollupDependencyGraph` over
`GetRollupArchiveRuntimeStore()`, the same way the asset-repository GraphQL field
`streamData.resolveSeriesQuery` and the MCP tool `resolve_series_query` do — which archive of the
family (the persisted base archive plus its transitive rollups) can answer the window at the
requested number of points. Whether that archive is then actually read is decided by the exactness
check further down. The effective `Limit` doubles as the target point count and the first column's
attribute path is the source path the resolver matches a rollup on.

The required aggregation is never guessed (decision O2): it is the first column's persisted
aggregation, or the node's optional `Aggregation` override. That override mirrors exactly what a
query definition can carry (Count, Minimum, Maximum, Average, Sum) and, when set, replaces the
aggregation on **every** column of the query — so the values read back are the ones the selected
rollup actually stores. The remaining members of the aggregation enum are rejected with an
actionable error: a time-weighted average and a state duration need per-column metadata (carry
lookback, comparison value) the node has no way to supply.

**The result never depends on which archive was read.** `From`, `To` and `Limit` are executed exactly as
the query defines them, and a rollup is only read when it answers the query *identically* to the archive
persisted on it — so running a query through this node and running it in the query editor can never
disagree. The bin geometry is never adjusted to fit the rollup.

The reason a rollup can disagree at all: the storage layer bins with an interval of `(To - From) / Limit`
anchored on `From`, and a windowed source only contributes a row to a bin when its whole window fits
inside it. A coarser rollup therefore reproduces the base archive exactly only when every stored bucket
lies completely inside one bin. The node checks that before accepting the rollup:

| Condition | Why |
|---|---|
| bin width is a whole multiple of the rollup's bucket size | otherwise one bucket per bin straddles a boundary and is dropped |
| `From` sits on the rollup's bucket grid (tick-anchored, as the write path aligns it) | otherwise *every* bucket straddles a boundary |
| alignment is fixed-size, not calendar (day / week / month / year) | a fixed-width bin cannot line up with civil buckets that shift with DST |
| the rollup's watermark covers `To` | otherwise the newest bins read low |

If any condition fails the persisted archive is read and the reason is logged as a warning, because it is
nearly always a query-definition detail the author can fix. Worked example from the field (AB#4725): a
7-day window with **10** buckets gives 16 h 48 min bins over an hourly rollup — not a multiple, so one
hour per bin would fall out (measured: 8 of 168 hours, −4.1 % on the total and −27 % on a single bin,
because the dropped hour may be a load peak). The same window with **12** buckets gives 14 h bins, the
rollup is used, and the values match the base archive to the last digit. As a rule of thumb: pick a
`Limit` that divides the range into whole multiples of the rollup's bucket size.

The archive actually queried is reported at info level together with the bin width and the rollup's
bucket size, so the origin of the numbers is visible without guesswork.

Every non-`Ok` signal (`ResolutionLimited`, `NoSuitableRollup`, `UnknownBaseGrain`, `EmptyLadder`) is
reported as a warning as well. The resolver's own point count is informational only and never overrides
the query's bucket count. `EmptyLadder` and a tenant without a rollup-archive store fall back to the
persisted archive, which is why a plain raw archive without rollups behaves exactly as it would without
any selection at all.

#### GetStreamDataNode

`GetStreamData@1` reads rows straight out of a stream data archive. It is the ad-hoc counterpart of
`GetQueryById@1`: archive, columns, time range, filters and sorting are configured on the node, so no
persisted query entity is needed. Writes a `QueryResult` (`Columns` + `Rows`) to `TargetPath`, which
`QueryResultToMarkdownTable@1` can consume directly.

| Parameter | Type | Description |
|-----------|------|-------------|
| `ArchiveRtId` | OctoObjectId | RtId of the archive to read. Must be activated. Required |
| `Columns` | collection\<string\> | Attribute paths to project (e.g. `Temperature`, `Amount.Value`), or a formula column's name. **Empty reads the whole archive**: every data column it declares, preceded by `WellKnownName` |
| `WellKnownNames` | collection\<string\> | Restrict to source entities with these well-known names — `Equals` for one value, `In` for several |
| `WellKnownNamesPath` | string | JSONPath alternative to `WellKnownNames`; accepts a scalar, an array or a multi-match path |
| `RtIds` / `RtIdsPath` | collection\<string\> / string | Restrict to these source entities. Emitted as an `In` filter on the identity column |
| `FieldFilters` | collection | Additional filters on projected or standard columns, AND-combined |
| `SortOrders` | collection | Sort order, by the column names as they appear in the result (see „Column names" below) |
| `Skip` / `Take` | int? | Offset / page size of the read |
| `From` / `To` | DateTime? | Time range (UTC). Both boundaries are independently optional; a one-sided range leaves the other open |
| `FromPath` / `ToPath` | string | Read the boundaries from the pipeline data instead |
| `Limit` | int? | Row cap. Must be greater than zero when set. Independent of `Skip`/`Take` |
| `LimitPath` | string | Read the row cap from the pipeline data |
| `GapsTargetPath` | string | Where to write the coverage report. Setting it turns gap detection on |
| `ExpectedInterval` | TimeSpan? | Interval the gap counts use, greater than zero. Defaults to the archive's declared period |
| `GapsOnly` | bool | Report gaps only, skip reading the data |
| `MaxGapScanRows` | int? | Row cap for the coverage scan (default 200000), greater than zero |

**Precedence and UTC.** Per value the literal wins over the JSONPath variant. Timestamps may be
ISO-8601 strings or date/time values; a value without a time-zone offset (`"2026-07-01T00:00:00"`) is
read as UTC, not as the adapter host's local time (AB#4734), so the queried window never shifts with
the server's zone. A path that resolves to nothing leaves the value unset and logs a warning; a value
that is present but not a date/time (resp. not an integer for `LimitPath`) fails the node rather than
silently widening the range.

**Result shape.** A leading `Timestamp` column, then the projected columns. On a windowed archive
(time-range or rollup) `WindowStart` and `WindowEnd` are inserted after `Timestamp` — those archives
have no `timestamp` column of their own, the storage layer aliases `window_end` as the time axis, and
the window columns only reach the result when they are projected explicitly. Values are keyed in the
engine result by their physical CrateDB column name; the node asks the storage layer's field resolver
for that key and maps it back, so the headers keep the attribute paths you configured.

**Reading a whole archive.** Leaving `Columns` unset projects every data column the archive declares
and adds a `WellKnownName` column ahead of them — reading an entire archive is almost always
about several source entities, and the name is what tells their rows apart. So the minimal
configuration, just an `archiveRtId`, already returns usable data:

```
Timestamp | WindowStart | WindowEnd | WellKnownName | Energy | DataQuality
```

Formula (computed) columns are included and are addressed by their **name**, not by an attribute path
— they have none. A column whose backfill has not committed yet is absent, because the storage layer
hides it until then; it appears once its backfill completes. When `Columns` *is* set, the list is
honoured exactly as given — no `WellKnownName` is added, and it can be requested there like any other
column (`rtWellKnownName`).

The one exception is the time axis and, on a windowed archive, the row window: `Timestamp`,
`WindowStart` and `WindowEnd` are emitted for every row regardless, so naming one of them in `Columns`
would only append a second, identical column. Such an entry is dropped instead — the column still
appears, just once. Both spellings are recognised, the result header and the physical name
(`window_start`), because the check runs on the resolved column.

> Note on formulas: a formula binds to the **physical** column names — lower-cased and dot-stripped —
> so a column at path `Amount.Value` is referenced as `amountvalue`, not `Amount.Value`. That concerns
> writing the formula in the Studio, not reading the column through this node.

**Column names in `SortOrders` and `FieldFilters`.** Use the names as they appear in the result:
`Timestamp`, `WindowStart` / `WindowEnd` (windowed archives only), `WellKnownName`, or any column the
archive declares. The node translates those onto the physical storage columns — `WindowStart` →
`window_start`, and `Timestamp` → `window_end` on a windowed archive, which has no `timestamp` column
of its own.

Names are resolved through the storage layer's own field resolver, which is also what makes a formula
column readable: after a formula change its physical column is versioned (`{base}__v{N}`) and cannot
be derived from the name (AB#4764).

The translation is what makes the node's own vocabulary work at all: the storage layer knows
`window_start`, not `WindowStart`, and its lookup is case-insensitive but not separator-insensitive.
Without it a sort on the header the node itself emits would not reach the column it names.

The node additionally rejects any name that is neither a result header nor a column of the archive,
and the error lists the names that would have worked. A sort on `WindowStart` against a *raw* archive
is refused for the same reason — that archive has no row window. This check began as a guard against
the storage layer **dropping an unresolvable name without raising anything** (a mistyped sort returned
rows in storage order, a mistyped filter returned too many rows); since AB#4765 the storage layer
validates and rejects on its own, so the node's check is now the earlier and better-worded of two —
it can name the pipeline, the node and the valid names as the caller writes them.

**Errors** surface through the standard pipeline-exception channel — stream data not enabled for the
tenant, archive not found, a malformed runtime id, an inverted time range, a non-positive `Limit`, an
unknown sort/filter column, and any storage-level failure — so there are no silent empty results.

**Gap detection** (AB#4728). Setting `GapsTargetPath` makes the node additionally check whether the
queried range is actually covered, and write a report there:

```yaml
- type: GetStreamData@1
  archiveRtId: <archiveRtId>
  from: 2026-07-01T00:00:00
  to:   2026-07-02T00:00:00
  targetPath:     $.data
  gapsTargetPath: $.gaps
```

Reported **per source entity** — a missing quarter-hour on one meter must not be hidden by another
meter delivering. Each series carries its gaps as time ranges with duration and missing-interval
count, plus `isComplete`; the report as a whole carries `seriesCount`, `seriesWithGapsCount` and its
own `isComplete`, so a following `If@1` can branch on one field.

How it works: every stored `[window_start, window_end)` in the range is clamped to it, overlapping
and adjacent windows are merged, and whatever the merge does not cover is a gap. That needs **no**
declared period — a `TimeRangeArchive`'s `Period` is advisory and may be null — and it copes with
windows of differing length. A known interval (`ExpectedInterval`, else the archive's `Period`) only
adds the counts; without one the gaps are still reported as ranges and the node warns once.

Three things worth knowing:

- **Requires a windowed archive** (time-range or rollup) and both time boundaries. A raw archive
  stores single timestamps and has no interval coverage to judge — the node refuses rather than
  inventing an answer.
- **It runs its own query**, deliberately separate from the data query, whose `Limit`/`Skip`/`Take`
  would hide rows and make the scan report gaps that are not there. `MaxGapScanRows` (default
  200 000) bounds it; exceeding the cap fails the node instead of reporting from a truncated scan.
  A non-positive cap or `ExpectedInterval` is rejected rather than quietly replaced by the default —
  a configured value that means nothing is a mistake worth naming. To scan without a practical cap,
  use the largest possible integer, not zero.
- **An entity that delivered nothing at all is invisible** to a coverage scan — it simply has no
  rows. Where `RtIds` names the expected entities, each one without rows is reported as a
  full-range gap instead; otherwise the limitation stands and the node logs it.

Overlapping windows are not gaps and do not fail anything — the storage concept allows them — but
they are flagged per series as `hasOverlaps` and warned about once, because a sum over them counts
the overlap twice.

Not in scope for this node: downsampling and resolution-aware rollup selection. Both are covered by
`GetQueryById@1` with a persisted `RtDownsamplingSdQuery`; `GetStreamData@1` always reads exactly the
archive it was configured with, so the numbers never depend on an archive choice made behind the
caller's back. See AB#4722 / AB#4726.

One consequence worth knowing: the node exposes no time-zone option, so the resolver resolves in UTC and a
calendar-aligned rollup stored in another zone is already excluded from the ladder. Since the exactness
check rejects calendar-aligned rollups regardless of their zone, that resolver detail cannot influence the
result at all — the only rungs ever read are fixed-size ones.

#### AggregateStreamDataNode

`AggregateStreamData@1` condenses archive columns into key figures over a time range — the sum of a
month's energy, the maximum data quality in that month. Sibling of `GetStreamData@1`, which returns
the rows themselves. Writes a `QueryResult` to `TargetPath`.

```yaml
- type: AggregateStreamData@1
  archiveRtId: <archiveRtId>
  aggregations:
    - attributePath: Energy
      function: SUM
    - attributePath: DataQuality
      function: MAXIMUM
  groupBy: [ rtId ]
  from: 2026-07-01T00:00:00
  to:   2026-08-01T00:00:00
  requireGapFree: true
  targetPath: $.monthly
```

| Parameter | Type | Description |
|-----------|------|-------------|
| `ArchiveRtId` | OctoObjectId | RtId of the archive to read. Must be activated. Required |
| `Aggregations` | collection | The key figures: `attributePath` + `function`. At least one. Required |
| `GroupBy` | collection\<string\> | Columns to group by, e.g. `rtId` for one row per source entity. Empty means a single row |
| `WellKnownNames` / `WellKnownNamesPath` | collection\<string\> / string | Restrict to these source entities by name |
| `RtIds` / `RtIdsPath` | collection\<string\> / string | Restrict to these source entities by runtime id |
| `FieldFilters` | collection | Additional filters, AND-combined |
| `From` / `To` (+ `…Path`) | DateTime? / string | Time range (UTC), both boundaries independently optional |
| `RequireGapFree` | bool | Only aggregate when the range is fully covered |
| `ExpectedInterval` | TimeSpan? | Interval the completeness check counts in. Defaults to the archive's period |
| `MaxGapScanRows` | int? | Row cap for the completeness check (default 200000) |

**Functions: `COUNT`, `MINIMUM`, `MAXIMUM`, `AVERAGE`, `SUM`.** `TIME_WEIGHTED_AVERAGE` and
`STATE_DURATION` are refused: they need metadata this node cannot carry — a comparison value, or a
raw archive's LOCF path — and their result keys follow different rules. Use `GetQueryById@1` with a
persisted query that defines the aggregation per column; the error message says so.

**Result shape.** Group-by columns first, then one column per key figure. Without `groupBy` there is
exactly one row, even when storage returned nothing — the values are then null rather than the row
being absent, so a downstream consumer always finds the shape it expects. Aggregating the same path
more than once appends the function to the header (`Energy (Minimum)`), because the result keys are
unique per function but a bare path header would not be.

**`RequireGapFree`** runs the coverage check from `GetStreamData@1`'s gap detection *before* the
aggregation, sharing its scan, row cap and interval fallback. If any entity is short, the node fails
and names the affected series with how much is missing and where the first hole starts. There are no
partial results: an incomplete month must not return a figure that looks valid but is too low. The
guard needs both boundaries and a windowed archive.

Three things it does not do, deliberately:

- **Overlapping windows do not fail the guard.** They are legal per the storage concept, and
  `requireGapFree` promises gap-freedom, not disjointness. They are warned about, because a `SUM`
  counts the overlap twice — react to `hasOverlaps` from `GetStreamData@1`'s report if you must.
- **`AVERAGE` is arithmetic, not time-weighted.** Identical for equal-length windows (the
  quarter-hour case); for variable lengths a time-weighted average is the correct mean, which means
  `GetQueryById@1`.
- **No sorting, paging or row cap.** They have no meaning for an aggregation and are absent from the
  configuration rather than present and ignored.

Column names in `GroupBy`, `FieldFilters` and the `attributePath` values follow the same result
vocabulary and translation as `GetStreamData@1`, with the same refusal for unknown names. On an
aggregation the stakes are higher: a dropped filter inflates the figure, and a dropped group-by
column collapses every group into one row. See AB#4722 / AB#4752.

#### BackfillFromRtEntityNode

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
| `Path` | string | Main content path. Optional — read only when it resolves to a string value; a non-string value at an explicit path is rendered as JSON. The default `"$"` (root object) is treated as "no main content" so **MCP-only** pipelines (no `path` set) work without error (AB#4313). |
| `Question` | string | Query to ask Claude |
| `DataPaths` | ICollection | Additional context data |
| `ApiKey` | string | Anthropic API key (prefer `ApiKeyConfigurationName`) |
| `Model` | string? | Claude model id. **No default** (a pinned id goes out of date). Resolved as `AiConfiguration.aiModel` → node `model`; if neither is set the node fails with a clear "AI model is required" error. |
| `ApiKeyConfigurationName` | string? | Well-known name of the `AiConfiguration` entity that supplies `apiKey`, `mcpServerUrl` **and `aiModel`** (all take precedence over the node's own values). |
| `McpServiceAccountConfigName` | string? | Well-known name of a `System.Communication/ServiceAccountConfiguration` entity. When set, the node acquires an OAuth2 client-credentials token and sends `Authorization: Bearer` on every MCP request — required once the MCP server enforces auth (AB#4315). |
| `TargetPath` | string | Response storage location |

> **MCP-only mode:** when `McpServerUrl` / `apiKeyConfigurationName` supplies MCP tools, the node needs no `path` — Claude queries live data via the tools. `ResolveMainContent` returns null for the default `"$"` root object instead of trying to read it as a string (which previously threw *"Cannot get the value of a token type 'StartObject' as a string"*).

> **Model resolution:** `apiKey`, `mcpServerUrl` **and** `aiModel` are all read from the referenced `AiConfiguration` entity (with node-config fallback), so the AI settings live in one place. `ResolveModel` prefers `AiConfiguration.aiModel`; there is deliberately no hard-coded default model.

> **MCP authentication:** the node authenticates to the OctoMesh MCP server via a `ServiceAccountConfiguration` (`McpServiceAccountConfigName`), reusing `IServiceAccountTokenService` (same mechanism as `DeployPipelineNode`). The acquired bearer token is attached to the `initialize` / `tools/list` / `tools/call` requests. Without a config name the calls are unauthenticated (local/dev only). Server-side enforcement is tracked in AB#4315.

> **JSON response recovery (`responseFormat: json`):** the node first tries to parse the whole response as JSON; if the model wrapped it in prose or a ```` ```json ```` fence, `ExtractJsonFromText` recovers the first top-level JSON value — **array `[…]` or object `{…}`, whichever comes first** — with string/escape-aware bracket matching. The array case is load-bearing for mapping pipelines: a prose-wrapped array (`"Here are the mappings: [ … ]"`) must be returned whole, not reduced to its first inner object (which made the downstream `ForEach` fail with *"value is not an array"*).

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

#### SaveStreamDataInArchiveNode

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
- **Transient-failure retry**: each send is retried up to 4 attempts with exponential backoff
  (2s → 4s → 8s) on transient SMTP failures (dropped/reset connection, relay throttling closing
  the connection mid-EHLO, socket errors). A fresh `SmtpClient` is created per attempt and the
  backoff is awaited outside the concurrency semaphore. Permanent recipient rejections
  (`SmtpFailedRecipientException`) are not retried. This prevents a single dropped connection
  from failing an entire `ForEach` batch of e-mails when only one message could not be delivered.

#### SftpUploadNode

Uploads files to an SFTP server. Supports both binary files from MongoDB storage and string content (e.g., CSV) from the pipeline data context.

| Parameter | Type | Description |
|-----------|------|-------------|
| `ServerConfiguration` | string | Global config reference for SFTP server |
| `RemoteDirectory` | string | Target directory on the SFTP server |
| `FileName` | string | Static file name (required: set `FileName` or `FileNamePath`) |
| `FileNamePath` | string | Dynamic file name from data context (required: set `FileName` or `FileNamePath`; takes precedence over `FileName`) |
| `FileRtId` | string | Static RtId of binary file (content source; exactly one source required) |
| `FileRtIdPath` | string | Dynamic RtId path in data context (content source; takes precedence over `FileRtId`) |
| `Path` | string | Data context path for string content (content source; exactly one source required) |
| `Encoding` | string | Encoding for string content (default `utf-8`; e.g. `windows-1252`, `iso-8859-1`). Validated when the pipeline configuration is bound — unknown names and byte-order-sensitive encodings (`utf-16*`, `utf-32*`) fail the deployment. Binary sources are uploaded byte-for-byte regardless. |
| `OnEncodingError` | enum | `Replace` (default): each unencodable character becomes one `?` and a warning lists the offending code points. `Fail`: the upload aborts before any data reaches the target. |

**Configuration rules**:
- You **must** configure a file name using either `FileName` (static) or `FileNamePath` (dynamic). If both are set, `FileNamePath` takes precedence.
- You **must** configure **exactly one** content source:
  - Binary content: `FileRtId` (static) or `FileRtIdPath` (dynamic).
  - String content: `Path` (data context path for the file contents).
- Providing no content source or both binary and string sources will cause a validation error at runtime.

**Features**:
- Password and private key authentication (at least one must be configured)
- Concurrent connection limit control (semaphore-based, thread-safe)
- Automatic remote directory creation
- Binary file upload from MongoDB large binary storage
- String content upload as file (e.g., CSV data)
- File name sanitization to prevent path traversal
- Optional host key pinning via `HostKeyFingerprint` in the server configuration

##### SFTP server configuration entry

All three SFTP nodes resolve their connection from the same global configuration entry:

```json
{
  "Host": "sftp.example.com",
  "Port": 22,
  "Username": "user",
  "Password": "...",
  "PrivateKey": "-----BEGIN OPENSSH PRIVATE KEY-----...",
  "PrivateKeyPassphrase": "...",
  "MaxConcurrentConnections": 3,
  "ConnectTimeoutSeconds": 30,
  "OperationTimeoutSeconds": 600,
  "WaitForSlotTimeoutSeconds": 300,
  "HostKeyFingerprint": "kSuxKMWLxOLE3nn3TxmXvJvI7NrHkGDhAo9SPHt9YQg"
}
```

`Password` or `PrivateKey` must be set. `MaxConcurrentConnections` bounds how many sessions are open against that server at a time; the counters live on the ETL context, so a redeployed pipeline picks up a changed limit. The three timeout values are optional and default to SSH.NET's own behaviour: 30 seconds to connect, and no limit at all on an individual listing, download or upload, nor on waiting for a free slot. Leaving them at zero therefore keeps a server that accepts the connection and then stalls holding its slot until the process restarts, which is why a server with predictable transfer sizes should set them. `HostKeyFingerprint` is the SHA-256 fingerprint of the expected host key, non-padded base64 exactly as `ssh-keygen -lf` prints it, with or without the `SHA256:` prefix; when it is set, a server presenting a different key is refused. When it is absent, any host key is accepted, which is how every release before this option behaved.

#### SftpListNode

Lists a remote directory over SFTP and emits one element per matching file. Metadata only; the content is read with `SftpDownloadNode`.

| Parameter | Type | Description |
|-----------|------|-------------|
| `ServerConfiguration` | string | Global config reference for SFTP server |
| `RemoteDirectory` | string | Directory to list |
| `FilePattern` | string | Glob: `*` any run of characters, `?` exactly one, anchored, case insensitive, everything else literal |
| `MinFileAgeSeconds` | int | Omit entries whose last write is younger, so a file still being written is picked up later (default 0) |
| `TargetPath` | string | Where the array is written |

Each element carries `name`, `fullPath`, `length`, `lastWriteTimeUtc` and a nested `source` object with `serverConfiguration`, `remoteDirectory` and `filePattern`.

**Features**:
- Directory entries excluded, result ordered by name (ordinal)
- Empty result still writes an empty array, so a downstream `ForEach@1` does not abort with `PathMustBeArray`
- `lastWriteTimeUtc` uses the round-trip format, so a consumer can build a stable file identity from it
- `source` stamp lets a consumer scope its bookkeeping without repeating the connection values

#### SftpDownloadNode

Downloads exactly one file and writes its decoded content to the target path. Read counterpart of `SftpUploadNode`.

| Parameter | Type | Description |
|-----------|------|-------------|
| `ServerConfiguration` | string | Global config reference for SFTP server |
| `RemotePath` | string | Static remote path (set this or `RemotePathPath`) |
| `RemotePathPath` | string | Data context path resolving to the remote path; takes precedence over `RemotePath` |
| `Encoding` | string | Encoding the remote file is written in, default `utf-8`; unknown names are rejected when the configuration is bound |
| `OnEncodingError` | enum | `Replace` (default): undecodable bytes become the replacement character and a warning is logged. `Fail`: the node aborts |
| `TargetPath` | string | Where the decoded content is written |

**Features**:
- One session per file, meant to run inside a `ForEach@1` over an `SftpList@1` result
- No dry-run branch: reading has no side effects and the downstream chain must see the content

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
  ├─ Extract Nodes (GetRtEntities*, GetAssociationTargets, SftpList, SftpDownload, etc.)
  ├─ Transform Nodes (DataMapping, CreateUpdateInfo, MakeHttpRequest, etc.)
  └─ Load Nodes (ApplyChanges, SaveStreamDataInArchive, EMailSender, SftpUpload)
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
| `AuthorityUrl` | string | `https://localhost:5003` | Identity service issuing the access tokens accepted by secured trigger nodes |
| `AuditAnonymousInvocations` | bool | `false` | Stores an event per invocation of an anonymous trigger route. Opt in per environment; the decision always reaches the adapter log at debug level regardless |
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
3. Authorize the caller unless the route was registered as anonymous
4. Parse request body based on Content-Type
5. Build the input `JsonObject` with path, method, body, query, files, formData, contentType
6. Execute pipeline with input
7. Return the resulting `JsonNode` as JSON

### Route Authorization

Routes are registered with an `AllowAnonymous` flag and a list of `RequiredRoles`. Anonymous
routes are served as before; all others are authorized in three steps and every decision is
both logged and written to the tenant's event log (see **Route audit** below):

1. A bearer token validated against `AuthorityUrl` (signature, issuer, audience, lifetime) —
   `401 Unauthorized` when missing or invalid.
2. The token's tenant must be the tenant this adapter serves — `403 Forbidden` otherwise. Only
   user tokens are compared: a client-credentials token carries neither a subject nor a tenant
   claim, so machine callers are authorized by role alone. `allowed_tenants` is deliberately
   ignored, it drives tenant selection rather than authorization (same rule as
   `TenantAuthorizationMiddleware` in octo-common-services). A user token without a tenant claim
   is rejected rather than waved through.
3. When roles are configured the caller must hold at least one — `403 Forbidden` otherwise.
   Without roles a valid token of the right tenant is enough.

| Node | Anonymous | Sees credential headers | Reason |
|------|-----------|-------------------------|--------|
| `FromHttpRequest@1` | yes | no | Deprecated, kept unchanged for existing pipelines |
| `FromHttpRequest@2` | configurable, default no | no | `AllowAnonymous` on the node |
| `FromTeamsBot@1` | yes | yes | Validates its own Bot Framework token, so it needs the raw header |

### Route audit

Each decision on a secured route is written to the tenant's event log as a
`System.Notification/Event` tagged with the `MeshAdapter` source, so an operator sees it in Studio
under **Repository → Events** filtered by *Adapter*. Denials are warnings, an authorized call is an
information entry, and every message names the decision, the route, the caller's subject and — where
known — the tenant and roles. The credential itself is never part of a message.

`IAdapterEventService` (`src/MeshAdapter.Sdk/Services/AdapterEventService.cs`) opens a scope per
event to reach the scoped `IEventRepository`, mirroring the communication controller's own event
service, and swallows any failure into a warning: an unavailable event log must never turn a trigger
invocation into a `500`. Only the repository is taken from the notification package —
`AddOctoNotification()` would additionally install a blueprint bootstrap, notification and markdown
services and replace the runtime engine's audit sink.

Anonymous routes are audited on two different granularities, because the two facts have very
different value and cost. The **existence** of an unauthenticated route is a standing exposure, so
it is recorded once per deployment as an information event (`Route POST /x registered without
authentication`) — unconditionally, since that is what an operator needs to find open doors.
Individual **invocations** carry no caller identity and serve public webhooks, so they are traced
at debug level and stored as an event only when `AuditAnonymousInvocations` is set. Both of those
sinks are quiet by default and each has its own switch: the log line is emitted unconditionally but
suppressed by the adapter's default minimum level, so it becomes visible once
`OCTO_ADAPTER__MINIMUMLOGLEVEL` is lowered to `Debug`, while the event needs the setting above.
What remains visible with neither switch touched is the registration event and the pipeline
execution row, the latter being the only per-invocation record that has retention.

The setting is off by default in every build and environment, and is opted into per environment —
`OCTO_ADAPTER__AUDITANONYMOUSINVOCATIONS=true` for a container, or
`--Adapter:AuditAnonymousInvocations=true` for an adapter started from a build directory, which is
how `Start-Octo` in octo-tools already passes `--Adapter:TenantId`. A build-conditional default was
deliberately rejected: it cannot be asserted from a test run (tests compile as `Debug`, so the
`Release` branch is unreachable) and it would have missed both development setups anyway, since
octo-tools defaults to running `Release` output and getting-started runs released images.

The cost this guards against was measured on a real half-year-old tenant: the event log ran at 39
entries/day totalling 704 KB on disk, and nothing prunes it — so a webhook taking one request per
minute would add roughly 37× the whole existing log every day.

The `Authorization`, `Proxy-Authorization` and `Cookie` headers are withheld from `input["headers"]`
on every route, unless the trigger node opted in via `ReceivesCredentialHeaders`. The data root is
echoed back in the response, can be persisted by nodes such as `SetPipelineExecutionResult@1`
(default `Path: $`) and is rendered in the Studio debug panel when debugging is enabled - so
forwarding the credential would put a replayable token in all three places.

Whether the adapter authorized the caller (`AllowAnonymous`) and whether the pipeline needs to see
the credential (`ReceivesCredentialHeaders`) are **separate questions**, and conflating them was a
defect: platform apps attach the operator's token per host rather than per route, so an anonymous
route routinely receives a token it never asked for. `FromTeamsBot@1` is the only trigger that opts
in, because Bot Framework issues its own tokens which the platform gate cannot validate, leaving the
node to check the header itself.

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

- MongoDB to MongoDB (BackfillFromRtEntityNode)
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
