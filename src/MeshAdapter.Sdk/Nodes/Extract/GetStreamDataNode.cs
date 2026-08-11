using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Reads rows from a stream data archive. The ad-hoc counterpart of <c>GetQueryById@1</c>: archive,
/// columns, time range and filters are configured on the node instead of being persisted as a query
/// entity. Reads exactly the configured archive — there is no resolution-aware rollup selection, so
/// the numbers never depend on which archive the node decided to read.
/// </summary>
/// <param name="next">Next node delegate in the pipeline</param>
/// <param name="context">Mesh ETL context</param>
/// <param name="systemContext">System context used to resolve the tenant-scoped stream-data repository</param>
[NodeConfiguration(typeof(GetStreamDataNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetStreamDataNode(
    NodeDelegate next,
    IMeshEtlContext context,
    ISystemContext systemContext)
    : IPipelineNode
{
    private const string TimestampHeader = "Timestamp";
    private const string WindowStartHeader = "WindowStart";
    private const string WindowEndHeader = "WindowEnd";
    private const string WellKnownNameHeader = "WellKnownName";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetStreamDataNodeConfiguration>();

        var (streamDataRepo, snapshot) = await ResolveArchiveAsync(c.ArchiveRtId, nodeContext);
        var resolver = StreamDataNodeHelpers.CreateFieldResolver(snapshot);

        // Literal configuration wins over the JSONPath variant; both boundaries are normalised to UTC
        // so the adapter's local zone can never shift the queried window (AB#4734).
        var from = StreamDataNodeHelpers.ToUtcOrNull(c.From)
                   ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.FromPath,
                       nameof(c.FromPath), "the start of the time range stays open.");
        var to = StreamDataNodeHelpers.ToUtcOrNull(c.To)
                 ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.ToPath,
                     nameof(c.ToPath), "the end of the time range stays open.");
        var limit = c.Limit
                    ?? StreamDataNodeHelpers.ResolveIntFromPath(dataContext, nodeContext, c.LimitPath,
                        nameof(c.LimitPath), "no row cap is applied.");

        if (from.HasValue && to.HasValue && from >= to)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataTimeRangeInvalid(nodeContext, from, to);
        }

        // The storage layer rejects a non-positive LIMIT; catching it here makes a misconfiguration a
        // pipeline error naming the property rather than an SQL-level failure.
        if (limit is <= 0)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataLimitInvalid(nodeContext, limit);
        }

        var detectGaps = !string.IsNullOrWhiteSpace(c.GapsTargetPath);
        if (c.GapsOnly && !detectGaps)
        {
            throw MeshAdapterPipelineExecutionException.GapsOnlyWithoutTarget(nodeContext);
        }

        if (detectGaps)
        {
            if (from is null || to is null)
            {
                throw MeshAdapterPipelineExecutionException.GapDetectionTimeRangeRequired(nodeContext);
            }

            if (!snapshot.UsesWindowedStorage)
            {
                throw MeshAdapterPipelineExecutionException.GapDetectionRequiresWindowedArchive(
                    nodeContext, c.ArchiveRtId);
            }

            // Both are rejected rather than quietly substituted: falling back to the default cap
            // would hide that the configured value means nothing, and treating a zero interval as
            // "none configured" would log a warning that sends the author looking in the wrong place.
            if (c.MaxGapScanRows is <= 0)
            {
                throw MeshAdapterPipelineExecutionException.GapScanRowLimitInvalid(nodeContext,
                    c.MaxGapScanRows);
            }

            if (c.ExpectedInterval is { Ticks: <= 0 })
            {
                throw MeshAdapterPipelineExecutionException.ExpectedIntervalInvalid(nodeContext,
                    c.ExpectedInterval.Value);
            }
        }

        // Resolved once and shared: both read JSONPath and warn on a path that resolves to nothing,
        // so doing it per query would repeat the work and log the same warning twice.
        var rtIds = StreamDataNodeHelpers.ResolveRtIds(c.RtIds, c.RtIdsPath, dataContext, nodeContext,
            nameof(c.RtIdsPath));
        var fieldFilters = StreamDataNodeHelpers.BuildFieldFilters(c.WellKnownNames,
            c.WellKnownNamesPath, c.FieldFilters, snapshot, resolver, dataContext, nodeContext,
            nameof(c.WellKnownNamesPath));

        if (!c.GapsOnly)
        {
            var (columns, columnsFromArchive) = ResolveColumns(c, snapshot, resolver, nodeContext);

            var options = StreamDataQueryOptions.Create()
                .WithCkTypeId(snapshot.TargetCkTypeId)
                // Windowed archives alias window_end as timestamp on read, but only explicitly
                // requested columns reach StreamDataRow.Values — so the window has to be asked for
                // by name.
                .WithColumns(BuildProjectedColumns(columns, snapshot))
                .WithRtIds(rtIds)
                // Both boundaries are independently optional; a one-sided range leaves the other open.
                .WithTimeRange(from, to)
                .WithLimit(limit)
                .WithSortOrders(BuildSortOrders(c, snapshot, resolver, nodeContext))
                .WithFieldFilters(fieldFilters)
                // Skip/Take map onto the paginated read (offset / page size); the row cap is Limit.
                .WithPagination(c.Skip, c.Take);

            var result = await ExecuteAsync(streamDataRepo, c.ArchiveRtId, options, nodeContext);

            nodeContext.Debug(
                $"Read {result.Rows.Count} row(s) of {result.TotalCount} from archive '{c.ArchiveRtId}'.");

            var queryResult = BuildQueryResult(columns, snapshot, result, columnsFromArchive);

            dataContext.Set(c.TargetPath, queryResult, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        if (detectGaps)
        {
            var report = await StreamDataGapScanner.ScanAsync(streamDataRepo, snapshot, c.ArchiveRtId,
                new StreamDataGapScanner.Request(from!.Value, to!.Value, c.ExpectedInterval,
                    c.MaxGapScanRows, rtIds, fieldFilters),
                nodeContext);

            dataContext.Set(c.GapsTargetPath!, report, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private static async Task<StreamDataQueryResult> ExecuteAsync(IStreamDataRepository streamDataRepo,
        OctoObjectId archiveRtId, StreamDataQueryOptions options, INodeContext nodeContext)
    {
        try
        {
            return await streamDataRepo.ExecuteQueryAsync(archiveRtId, options);
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(nodeContext,
                archiveRtId, ex);
        }
    }

    /// <summary>
    /// Resolves the tenant's stream-data repository and the archive's snapshot. The snapshot supplies
    /// the CkTypeId every query option requires, plus the storage shape that decides whether the row
    /// window is available as separate columns.
    /// </summary>
    private async Task<(IStreamDataRepository Repository, ArchiveSnapshot Snapshot)> ResolveArchiveAsync(
        OctoObjectId archiveRtId, INodeContext nodeContext)
    {
        var tenantId = context.TenantId;
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext,
                             tenantId);

        var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                       ?? throw MeshAdapterPipelineExecutionException.ArchiveNotFound(nodeContext,
                           archiveRtId);

        return (repository, snapshot);
    }

    /// <summary>
    /// One projected column: the header the result keeps, the name handed to the query, and the key
    /// the row values carry it under.
    /// </summary>
    private readonly record struct ProjectedColumn(string Header, string QueryName, string StorageKey);

    /// <summary>
    /// The columns to project. Configured columns win; leaving them unset reads the whole archive —
    /// anything else would make the minimal configuration (just an archive id) return nothing but the
    /// time axis.
    /// <para>
    /// Computed columns are included now that the field resolver supplies their storage key
    /// (AB#4764); before that their versioned physical name could not be reproduced and they were
    /// skipped. A column mid-backfill is still absent, because the resolver does not register it —
    /// the read path hides it until the backfill commits.
    /// </para>
    /// </summary>
    /// <returns>
    /// The columns, and whether they were derived from the archive rather than configured — the caller
    /// adds the well-known name only in that case, where the intent is "show me this archive".
    /// </returns>
    private static (List<ProjectedColumn> Columns, bool FromArchive) ResolveColumns(
        GetStreamDataNodeConfiguration c, ArchiveSnapshot snapshot, StreamDataFieldResolver resolver,
        INodeContext nodeContext)
    {
        var configured = c.Columns?.Where(col => !string.IsNullOrWhiteSpace(col)).ToList() ?? [];
        if (configured.Count != 0)
        {
            var reserved = ReservedQueryNames(snapshot);

            var columns = configured
                .Select(col =>
                {
                    var resolved = StreamDataNodeHelpers.ResolveQueryableColumn(col, snapshot, resolver,
                        nodeContext, "projection");
                    // The header keeps what the caller asked for, so it recognises its own request.
                    return new ProjectedColumn(col, resolved.QueryName, resolved.StorageKey);
                })
                // Naming the time axis or the row window adds nothing — they are emitted for every row
                // anyway — but it would append a second, identical column to the result. Dropping the
                // redundant entry costs the caller nothing: the column still appears, just once. The
                // test is the resolved name, so the physical spelling (window_start) is caught as well
                // as the result header (WindowStart).
                .Where(col => !reserved.Contains(col.QueryName))
                .ToList();

            // An explicit list that reduces to nothing stays explicit — it must not fall through to
            // "read the whole archive", which is a different request entirely.
            return (columns, false);
        }

        var fromArchive = StreamDataNodeHelpers.ResolveArchiveColumns(snapshot, resolver)
            .Select(col => new ProjectedColumn(col.QueryName, col.QueryName, col.StorageKey))
            .ToList();

        return (fromArchive, true);
    }

    /// <summary>
    /// The columns <see cref="BuildQueryResult" /> emits for every row without being asked for them:
    /// the time axis, and on a windowed archive the row window as well. A configured column that
    /// resolves to one of these is redundant rather than wrong, so it is dropped instead of doubling
    /// the column in the result.
    /// </summary>
    private static HashSet<string> ReservedQueryNames(ArchiveSnapshot snapshot)
        => snapshot.UsesWindowedStorage
            ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                StreamDataNodeHelpers.WindowStartColumn,
                StreamDataNodeHelpers.WindowEndColumn
            }
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                StreamDataNodeHelpers.TimestampColumn
            };

    /// <summary>
    /// The columns actually requested from the storage layer: the projected names plus, on a windowed
    /// archive, the row-window columns so they can be surfaced in the result.
    /// </summary>
    private static List<string> BuildProjectedColumns(List<ProjectedColumn> columns,
        ArchiveSnapshot snapshot)
    {
        var names = columns.Select(col => col.QueryName);

        if (!snapshot.UsesWindowedStorage)
        {
            return names.ToList();
        }

        return new List<string>
            {
                StreamDataNodeHelpers.WindowStartColumn,
                StreamDataNodeHelpers.WindowEndColumn
            }
            .Concat(names)
            .ToList();
    }

    /// <summary>
    /// The configured sort orders, with their column names translated to the physical ones. Without
    /// that translation a sort on a result header such as <c>WindowStart</c> is dropped by the
    /// storage layer without a word and the rows come back in storage order.
    /// </summary>
    private static IReadOnlyList<SortOrderItem>? BuildSortOrders(GetStreamDataNodeConfiguration c,
        ArchiveSnapshot snapshot, StreamDataFieldResolver resolver, INodeContext nodeContext)
    {
        var sortOrders = c.SortOrders.GetSortOrderItems();
        if (sortOrders is not { Count: > 0 })
        {
            return null;
        }

        return sortOrders
            .Select(sort => new SortOrderItem(
                StreamDataNodeHelpers.ResolveQueryableColumn(sort.AttributePath, snapshot, resolver,
                    nodeContext, "sorting").QueryName,
                sort.SortOrder))
            .ToList();
    }

    /// <summary>
    /// Maps the rows into the tabular result shape the pipeline works with, so
    /// <c>QueryResultToMarkdownTable@1</c> can be chained directly. Leading Timestamp column (the time
    /// axis — window end on a windowed archive), then the row window on windowed archives, then the
    /// well-known name when the whole archive is being read, then the projected attribute columns.
    /// <para>
    /// <c>includeWellKnownName</c> is set when the columns were derived from the archive rather than
    /// configured. Reading a whole archive is almost always about several source entities, and the
    /// well-known name is what tells their rows apart — but when the caller listed the columns
    /// explicitly, that list is honoured as given (<c>rtWellKnownName</c> can be named there like any
    /// other column).
    /// </para>
    /// </summary>
    private static QueryResult BuildQueryResult(List<ProjectedColumn> columns, ArchiveSnapshot snapshot,
        StreamDataQueryResult result, bool includeWellKnownName)
    {
        var queryResult = new QueryResult();

        queryResult.Columns.Add(new QueryResultColumns { Header = TimestampHeader });
        if (snapshot.UsesWindowedStorage)
        {
            queryResult.Columns.Add(new QueryResultColumns { Header = WindowStartHeader });
            queryResult.Columns.Add(new QueryResultColumns { Header = WindowEndHeader });
        }

        if (includeWellKnownName)
        {
            queryResult.Columns.Add(new QueryResultColumns { Header = WellKnownNameHeader });
        }

        queryResult.Columns.AddRange(columns.Select(col =>
            new QueryResultColumns { Header = col.Header }));

        foreach (var row in result.Rows)
        {
            var values = new List<object?> { row.Timestamp };

            if (snapshot.UsesWindowedStorage)
            {
                values.Add(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values,
                    StreamDataNodeHelpers.WindowStartColumn));
                values.Add(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values,
                    StreamDataNodeHelpers.WindowEndColumn));
            }

            if (includeWellKnownName)
            {
                // Read straight off the row like Timestamp — the storage layer always populates it,
                // so it needs no projection.
                values.Add(row.RtWellKnownName);
            }

            values.AddRange(columns.Select(col =>
                StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, col.StorageKey)));

            queryResult.Rows.Add(new QueryResultRow
            {
                RtId = row.RtId,
                CkTypeId = row.CkTypeId,
                Values = values
            });
        }

        return queryResult;
    }
}
