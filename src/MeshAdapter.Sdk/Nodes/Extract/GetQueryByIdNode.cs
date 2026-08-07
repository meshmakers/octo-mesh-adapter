using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Node get query by id. Supports runtime-data queries (simple, aggregation, grouped aggregation)
/// and stream-data queries (simple, aggregation, grouped aggregation, downsampling). The caller does
/// not need to know the query kind in advance — the persisted query entity (a shared
/// <see cref="RtPersistentQuery"/> subtype) is resolved and dispatched based on its concrete type.
/// A downsampling query is always routed through resolution-aware archive selection, which reads the
/// coarsest rollup of the archive family that answers the query <b>identically</b> to the archive
/// persisted on it — so the node and the query editor never disagree on the numbers.
/// </summary>
/// <param name="next">Next node delegate in the pipeline</param>
/// <param name="context">Mesh ETL context</param>
/// <param name="ckCacheService">Construction Kit cache service</param>
/// <param name="systemContext">System context used to resolve the tenant-scoped stream-data repository</param>
[NodeConfiguration(typeof(GetQueryByIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetQueryByIdNode(
    NodeDelegate next,
    IMeshEtlContext context,
    ICkCacheService ckCacheService,
    ISystemContext systemContext)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetQueryByIdNodeConfiguration>();

        // Disposed on every exit path (runtime, stream-data, and the QueryNotFound throw) so the
        // session is never left alive until GC.
        using var session = await context.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var rtQuery =
            await context.TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(
                session, c.QueryRtId);

        if (rtQuery == null)
        {
            throw MeshAdapterPipelineExecutionException.QueryNotFound(nodeContext, c.QueryRtId);
        }

        // Stream-data queries share the RtPersistentQuery base but are executed against the
        // tenant's stream-data repository (CrateDB) rather than the runtime graph query. Handle
        // them on a dedicated path; the runtime-graph switch below only knows the *RtQuery types.
        if (rtQuery is RtStreamDataQuery streamDataQuery)
        {
            // The load transaction is only needed to read the query entity; the actual stream-data
            // query does not run through the Mongo session, so release it before executing.
            await session.CommitTransactionAsync();

            await ProcessStreamDataQueryAsync(streamDataQuery, dataContext, nodeContext, c);
            await next(dataContext, nodeContext);
            return;
        }

        var queryOptions = RtEntityQueryOptions.Create().WithCachingDisabled();
        bool isAggregationQuery;
        IEnumerable<string> columnPaths;
        string queryCkTypeId;

        switch (rtQuery)
        {
            case RtSimpleRtQuery simpleQuery:
                isAggregationQuery = false;
                queryCkTypeId = simpleQuery.QueryCkTypeId;
                columnPaths = simpleQuery.Columns;
                ConfigureSimpleQueryOptions(simpleQuery, queryOptions);
                break;

            case RtAggregationRtQuery aggregationQuery:
                isAggregationQuery = true;
                queryCkTypeId = aggregationQuery.QueryCkTypeId;
                columnPaths = aggregationQuery.Columns.Select(col => col.AttributePath);
                ConfigureAggregationQueryOptions(aggregationQuery, queryOptions);
                break;

            case RtGroupingAggregationRtQuery groupedQuery:
                isAggregationQuery = true;
                queryCkTypeId = groupedQuery.QueryCkTypeId;
                var groupingColumns = groupedQuery.GroupingColumns?.ToList() ?? [];
                columnPaths = groupingColumns.Concat(
                    groupedQuery.Columns.Select(col => col.AttributePath));
                ConfigureGroupingAggregationQueryOptions(groupedQuery, groupingColumns, queryOptions);
                break;

            default:
                throw MeshAdapterPipelineExecutionException.UnsupportedQueryType(nodeContext,
                    rtQuery.GetType().Name);
        }

        // Add field filters from the pipeline configuration
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);

        // Include field filter paths in navigation pair resolution so that filters on
        // navigated attributes (e.g. 'parent.association->attribute') produce the required
        // navigation pairs. The overload also handles :: association meta filters.
        var fieldFilters = queryOptions.FieldFilters ?? new List<FieldFilter>();
        var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
            context.TenantRepository.TenantId, queryCkTypeId, columnPaths, fieldFilters);

        // For aggregation queries, don't pass skip/take to the database — paging is not applicable
        // or is applied in-memory for grouped aggregation results
        var resultSet = await context.TenantRepository.GetRtEntitiesGraphByTypeAsync(session, queryCkTypeId,
            queryOptions, roleIdDirectionPairs,
            isAggregationQuery ? null : c.Skip,
            isAggregationQuery ? null : c.Take);

        await session.CommitTransactionAsync();

        QueryResult queryResult = new();

        switch (rtQuery)
        {
            case RtSimpleRtQuery simpleQuery:
                BuildSimpleQueryResult(simpleQuery, resultSet, queryResult);
                break;

            case RtAggregationRtQuery aggregationQuery:
                if (resultSet.AggregationResult == null)
                {
                    throw MeshAdapterPipelineExecutionException.AggregationResultNull(nodeContext,
                        c.QueryRtId);
                }

                BuildAggregationQueryResult(aggregationQuery, resultSet.AggregationResult, queryResult);
                break;

            case RtGroupingAggregationRtQuery groupedQuery:
                if (resultSet.FieldAggregationResult == null)
                {
                    throw MeshAdapterPipelineExecutionException.FieldAggregationResultNull(nodeContext,
                        c.QueryRtId);
                }

                BuildGroupingAggregationQueryResult(groupedQuery, resultSet.FieldAggregationResult,
                    c.Skip, c.Take, queryResult);
                break;
        }

        dataContext.Set(c.TargetPath, queryResult, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static void ConfigureSimpleQueryOptions(RtSimpleRtQuery simpleQuery,
        RtEntityQueryOptions queryOptions)
    {
        if (simpleQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in simpleQuery.FieldFilter)
            {
                queryOptions.AddFieldFilter(fieldFilter.AttributePath, (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        if (simpleQuery.Sorting != null)
        {
            foreach (var orderItemRecord in simpleQuery.Sorting)
            {
                queryOptions.SortOrder(orderItemRecord.AttributePath, (SortOrders)orderItemRecord.SortOrder);
            }
        }

        if (simpleQuery.AttributeSearchFilter != null)
        {
            queryOptions.AttributeSearch(simpleQuery.AttributeSearchFilter.AttributePaths,
                simpleQuery.AttributeSearchFilter.SearchValue);
        }

        if (simpleQuery.TextSearchFilter != null)
        {
            queryOptions.TextSearch(simpleQuery.TextSearchFilter.SearchValue);
        }
    }

    private static void ConfigureAggregationQueryOptions(RtAggregationRtQuery aggregationQuery,
        RtEntityQueryOptions queryOptions)
    {
        if (aggregationQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in aggregationQuery.FieldFilter)
            {
                queryOptions.AddFieldFilter(fieldFilter.AttributePath, (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        var aggregateResult = queryOptions.AggregateResult();
        foreach (var column in aggregationQuery.Columns)
        {
            AddAggregation(aggregateResult, column.AttributePath, column.AggregationType);
        }
    }

    private static void ConfigureGroupingAggregationQueryOptions(
        RtGroupingAggregationRtQuery groupedQuery, List<string> groupingColumns,
        RtEntityQueryOptions queryOptions)
    {
        if (groupedQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in groupedQuery.FieldFilter)
            {
                queryOptions.AddFieldFilter(fieldFilter.AttributePath, (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        var aggregateFieldGroupBy = queryOptions.AggregateFieldGroupBy(groupingColumns.ToArray());
        foreach (var column in groupedQuery.Columns)
        {
            AddAggregation(aggregateFieldGroupBy, column.AttributePath, column.AggregationType);
        }
    }

    private void BuildSimpleQueryResult(RtSimpleRtQuery simpleQuery,
        IResultSet<RtEntityGraphItem> resultSet, QueryResult queryResult)
    {
        queryResult.Columns.AddRange(simpleQuery.Columns.Select(column => new QueryResultColumns
            { Header = column }));
        queryResult.Rows.AddRange(resultSet.Items.Select(entity => new QueryResultRow
        {
            RtId = entity.RtId,
            CkTypeId = entity.CkTypeId ?? throw new Exception("CkTypeId is null"),
            Values = simpleQuery.Columns.Select(column =>
                entity.GetAttributeValueByAccessPath(ckCacheService, context.TenantId, column)).ToList()
        }));
    }

    private static void BuildAggregationQueryResult(RtAggregationRtQuery aggregationQuery,
        AggregationResult aggregationResult, QueryResult queryResult)
    {
        queryResult.Columns.AddRange(aggregationQuery.Columns.Select(column =>
            new QueryResultColumns { Header = column.AttributePath }));

        var row = new QueryResultRow();
        foreach (var column in aggregationQuery.Columns)
        {
            row.Values.Add(GetAggregationValue(aggregationResult, column.AttributePath,
                column.AggregationType));
        }

        queryResult.Rows.Add(row);
    }

    private static void BuildGroupingAggregationQueryResult(
        RtGroupingAggregationRtQuery groupedQuery,
        IEnumerable<FieldAggregationResult> fieldAggregationResults,
        int? skip, int? take, QueryResult queryResult)
    {
        var groupingColumns = groupedQuery.GroupingColumns?.ToList() ?? [];

        // Columns: groupBy columns first, then aggregation columns
        queryResult.Columns.AddRange(groupingColumns.Select(col =>
            new QueryResultColumns { Header = col }));
        queryResult.Columns.AddRange(groupedQuery.Columns.Select(column =>
            new QueryResultColumns { Header = column.AttributePath }));

        // Apply in-memory paging for grouped aggregation results
        IEnumerable<FieldAggregationResult> pagedResults = fieldAggregationResults;
        if (skip.HasValue)
            pagedResults = pagedResults.Skip(skip.Value);
        if (take.HasValue)
            pagedResults = pagedResults.Take(take.Value);

        foreach (var fieldAggResult in pagedResults)
        {
            var row = new QueryResultRow();

            // Add group key values
            var keys = fieldAggResult.Keys.ToList();
            for (var i = 0; i < groupingColumns.Count; i++)
            {
                row.Values.Add(i < keys.Count ? keys[i] : null);
            }

            // Add aggregation values
            foreach (var column in groupedQuery.Columns)
            {
                row.Values.Add(GetAggregationValue(fieldAggResult, column.AttributePath,
                    column.AggregationType));
            }

            queryResult.Rows.Add(row);
        }
    }

    private static void AddAggregation(AggregationInput aggregationInput, string attributePath,
        Enum aggregationType)
    {
        switch (aggregationType.ToString())
        {
            case "Count":
                aggregationInput.CountAttributePaths(attributePath);
                break;
            case "Sum":
                aggregationInput.SumAttributePaths(attributePath);
                break;
            case "Average":
                aggregationInput.AvgAttributePaths(attributePath);
                break;
            case "Minimum":
                aggregationInput.MinAttributePaths(attributePath);
                break;
            case "Maximum":
                aggregationInput.MaxAttributePaths(attributePath);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType,
                    $"Unknown aggregation type: {aggregationType}");
        }
    }

    private static object? GetAggregationValue(AggregationResult result, string attributePath,
        Enum aggregationType)
    {
        return aggregationType.ToString() switch
        {
            "Count" => result.CountStatistics.FirstOrDefault(a => a.AttributePath == attributePath)
                ?.Value,
            "Sum" => result.SumStatistics.FirstOrDefault(a => a.AttributePath == attributePath)?.Value,
            "Average" => result.AvgStatistics.FirstOrDefault(a => a.AttributePath == attributePath)
                ?.Value,
            "Minimum" => result.MinStatistics.FirstOrDefault(a => a.AttributePath == attributePath)
                ?.Value,
            "Maximum" => result.MaxStatistics.FirstOrDefault(a => a.AttributePath == attributePath)
                ?.Value,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType,
                $"Unknown aggregation type: {aggregationType}")
        };
    }

    private async Task ProcessStreamDataQueryAsync(RtStreamDataQuery query,
        IDataContext dataContext, INodeContext nodeContext, GetQueryByIdNodeConfiguration c)
    {
        var (tenantContext, streamDataRepo) = await ResolveStreamDataContextAsync(nodeContext);

        if (string.IsNullOrWhiteSpace(query.ArchiveRtId))
        {
            throw MeshAdapterPipelineExecutionException.ArchiveRtIdMissing(nodeContext, c.QueryRtId);
        }

        var archiveRtId = new OctoObjectId(query.ArchiveRtId);

        // Resolved once for every stream-data query kind: literal config value, else the value read
        // from the data context via *Path, else (in the executors) the value persisted on the query.
        var overrides = ResolveStreamDataOverrides(dataContext, nodeContext, c);

        var queryResult = query switch
        {
            RtSimpleSdQuery simple =>
                await ExecuteSimpleStreamDataQueryAsync(simple, archiveRtId, streamDataRepo, dataContext, c,
                    overrides, nodeContext),
            RtAggregationSdQuery aggregation =>
                await ExecuteAggregationStreamDataQueryAsync(aggregation, archiveRtId, streamDataRepo, dataContext,
                    c, overrides, nodeContext),
            RtGroupingAggregationSdQuery grouped =>
                await ExecuteGroupedAggregationStreamDataQueryAsync(grouped, archiveRtId, streamDataRepo,
                    dataContext, c, overrides, nodeContext),
            RtDownsamplingSdQuery downsampling =>
                await ExecuteDownsamplingStreamDataQueryAsync(downsampling, archiveRtId, streamDataRepo,
                    tenantContext, dataContext, c, overrides, nodeContext),
            // Any future stream-data query type is not yet supported.
            _ => throw MeshAdapterPipelineExecutionException.UnsupportedQueryType(nodeContext,
                query.GetType().Name)
        };

        dataContext.Set(c.TargetPath, queryResult, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
    }

    private async Task<QueryResult> ExecuteSimpleStreamDataQueryAsync(RtSimpleSdQuery query,
        OctoObjectId archiveRtId, IStreamDataRepository streamDataRepo, IDataContext dataContext,
        GetQueryByIdNodeConfiguration c, StreamDataOverrides overrides, INodeContext nodeContext)
    {
        var rtIds = query.RtIds?.Select(id => new OctoObjectId(id)).ToList();
        var sortOrders = query.Sorting?
            .Select(s => new SortOrderItem(s.AttributePath, (SortOrders)(int)s.SortOrder))
            .ToList();

        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(query.QueryCkTypeId)
            .WithColumns(query.Columns?.ToList() ?? [])
            .WithRtIds(rtIds)
            // Values from the node configuration win over the values persisted on the query.
            .WithTimeRange(overrides.From ?? query.From, overrides.To ?? query.To)
            .WithLimit(overrides.Limit ?? (query.Limit.HasValue ? (int)query.Limit.Value : null))
            .WithSortOrders(sortOrders)
            // Persisted field filters AND-combined with the node's configured filters.
            .WithFieldFilters(BuildStreamDataFieldFilters(query.FieldFilter, dataContext, c))
            // Skip/Take map onto the paginated read (offset / page size); the row cap is Limit.
            .WithPagination(c.Skip, c.Take);

        var result = await ExecuteAsync(() => streamDataRepo.ExecuteQueryAsync(archiveRtId, options),
            nodeContext, c);

        return BuildSimpleStreamDataQueryResult(query, result);
    }

    private async Task<QueryResult> ExecuteAggregationStreamDataQueryAsync(RtAggregationSdQuery query,
        OctoObjectId archiveRtId, IStreamDataRepository streamDataRepo, IDataContext dataContext,
        GetQueryByIdNodeConfiguration c, StreamDataOverrides overrides, INodeContext nodeContext)
    {
        var rtIds = query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(query.QueryCkTypeId)
            .WithAggregationColumns(BuildStreamAggregationColumns(query.Columns))
            .WithRtIds(rtIds)
            .WithTimeRange(overrides.From ?? query.From, overrides.To ?? query.To)
            .WithFieldFilters(BuildStreamDataFieldFilters(query.FieldFilter, dataContext, c));

        var result = await ExecuteAsync(
            () => streamDataRepo.ExecuteAggregationQueryAsync(archiveRtId, options), nodeContext, c);

        return BuildAggregationStreamDataQueryResult(query, result);
    }

    private async Task<QueryResult> ExecuteGroupedAggregationStreamDataQueryAsync(
        RtGroupingAggregationSdQuery query, OctoObjectId archiveRtId, IStreamDataRepository streamDataRepo,
        IDataContext dataContext, GetQueryByIdNodeConfiguration c, StreamDataOverrides overrides,
        INodeContext nodeContext)
    {
        var groupingColumns = query.GroupingColumns?.ToList() ?? [];
        var rtIds = query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(query.QueryCkTypeId)
            .WithGroupByColumns(groupingColumns)
            .WithAggregationColumns(BuildStreamAggregationColumns(query.Columns))
            .WithRtIds(rtIds)
            .WithTimeRange(overrides.From ?? query.From, overrides.To ?? query.To)
            .WithFieldFilters(BuildStreamDataFieldFilters(query.FieldFilter, dataContext, c));

        var result = await ExecuteAsync(
            () => streamDataRepo.ExecuteGroupedAggregationQueryAsync(archiveRtId, options), nodeContext, c);

        return BuildGroupedAggregationStreamDataQueryResult(query, groupingColumns, result);
    }

    /// <summary>
    /// Executes a persisted downsampling query: DATE_BIN bucketing over the window with one aggregate
    /// per persisted column, one row per bin (empty bins included, with null aggregates). The engine
    /// requires From, To and a positive bucket count, so the effective values are validated here
    /// rather than letting a storage-layer exception surface. The archive is re-routed by the series
    /// resolver first — resolution-aware selection is inherent to a downsampling query and not separately
    /// switchable — while the window and bucket count stay exactly as the query defines them, and a rollup
    /// is only read when it answers the query identically (see <see cref="ResolveEffectiveArchiveAsync" />).
    /// </summary>
    private async Task<QueryResult> ExecuteDownsamplingStreamDataQueryAsync(
        RtDownsamplingSdQuery query, OctoObjectId archiveRtId, IStreamDataRepository streamDataRepo,
        ITenantContext tenantContext, IDataContext dataContext, GetQueryByIdNodeConfiguration c,
        StreamDataOverrides overrides, INodeContext nodeContext)
    {
        var persistedColumns = query.Columns.ToList();
        if (persistedColumns.Count == 0)
        {
            // Without an aggregation the storage layer leaves the generate_series bin path and emits a
            // zero-length interval — an SQL error rather than an empty result.
            throw MeshAdapterPipelineExecutionException.DownsamplingColumnsMissing(nodeContext, c.QueryRtId);
        }

        // The overrides are already UTC; the wrap covers the persisted values, which come back from
        // Mongo as Kind=Local and have to be a real UTC instant for the window validation below and
        // for the bucket-grid comparison in RollupAnswersExactly.
        var from = StreamDataNodeHelpers.ToUtcOrNull(overrides.From ?? query.From);
        var to = StreamDataNodeHelpers.ToUtcOrNull(overrides.To ?? query.To);
        var limit = overrides.Limit ?? (query.Limit.HasValue ? (int)query.Limit.Value : null);

        if (from is null || to is null || from >= to)
        {
            throw MeshAdapterPipelineExecutionException.DownsamplingTimeRangeInvalid(
                nodeContext, c.QueryRtId, from, to);
        }

        if (limit is null or <= 0)
        {
            throw MeshAdapterPipelineExecutionException.DownsamplingLimitInvalid(nodeContext, c.QueryRtId, limit);
        }

        var rtIds = query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        // A node-configured aggregation replaces the persisted one on every column; otherwise each
        // column keeps its own. The archive selection matches a rollup on the first column — its
        // attribute path plus that column's (possibly overridden) aggregation.
        var aggregationOverride = MapAggregationOverride(c.Aggregation, nodeContext);
        var columns = BuildDownsamplingColumns(persistedColumns, aggregationOverride);
        var sourcePath = persistedColumns[0].AttributePath;

        var resolution = await ResolveSeriesAsync(
            aggregationOverride ?? persistedColumns[0].AggregationType, sourcePath, archiveRtId, rtIds,
            from.Value, to.Value, limit.Value, tenantContext, c, nodeContext);

        // Only the archive is re-routed, and only when the rollup provably answers the query
        // identically — the window and bucket count stay exactly as the query defines them.
        var effectiveArchiveRtId = resolution is null
            ? archiveRtId
            : await ResolveEffectiveArchiveAsync(resolution, archiveRtId, from.Value, to.Value,
                limit.Value, tenantContext, nodeContext);

        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(query.QueryCkTypeId)
            .WithAggregationColumns(columns
                .Select(col => new AggregationColumn(col.AttributePath, col.Function))
                .ToList())
            .WithRtIds(rtIds)
            .WithTimeRange(from.Value, to.Value)
            .WithLimit(limit.Value)
            // Persisted field filters AND-combined with the node's configured filters.
            .WithFieldFilters(BuildStreamDataFieldFilters(query.FieldFilter, dataContext, c));
        // Deliberately no WithPagination: the storage layer's downsampling path returns before the
        // generic LIMIT/OFFSET is applied, so Skip/Take are paged over the returned bins instead.

        var result = await ExecuteAsync(
            () => streamDataRepo.ExecuteDownsamplingQueryAsync(effectiveArchiveRtId, options), nodeContext, c);

        return BuildDownsamplingStreamDataQueryResult(columns, result, c);
    }

    private static async Task<StreamDataQueryResult> ExecuteAsync(
        Func<Task<StreamDataQueryResult>> execute, INodeContext nodeContext, GetQueryByIdNodeConfiguration c)
    {
        try
        {
            return await execute();
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataQueryFailed(nodeContext, c.QueryRtId, ex);
        }
    }

    /// <summary>
    /// Effective time range and row cap for a stream-data query, as configured on the node — either
    /// literally or resolved from the pipeline data via <c>FromPath</c> / <c>ToPath</c> /
    /// <c>LimitPath</c>. A <c>null</c> member means "not overridden": the caller falls back to the
    /// value persisted on the query entity. <see cref="From" /> and <see cref="To" /> are UTC by
    /// construction — see <see cref="ResolveStreamDataOverrides" /> — so an executor can hand them to
    /// the storage layer unchanged.
    /// </summary>
    private readonly record struct StreamDataOverrides(DateTime? From, DateTime? To, int? Limit);

    /// <summary>
    /// Resolves the stream-data overrides. Precedence per value: the literal configuration value wins
    /// over the path-resolved value, which wins over the persisted value (applied by the callers).
    /// The literal is checked first so an existing configuration keeps behaving identically even when
    /// a path is configured alongside it.
    /// Both boundaries are normalised to UTC here — the literal via
    /// <see cref="StreamDataNodeHelpers.ToUtcOrNull" />, the path value inside
    /// <see cref="StreamDataNodeHelpers.ResolveDateTimeFromPath" /> — so every stream-data executor
    /// inherits the node's UTC contract instead of having to remember it.
    /// </summary>
    private static StreamDataOverrides ResolveStreamDataOverrides(IDataContext dataContext,
        INodeContext nodeContext, GetQueryByIdNodeConfiguration c)
    {
        const string fallbackHint = "using the value persisted on the query.";

        return new StreamDataOverrides(
            StreamDataNodeHelpers.ToUtcOrNull(c.From)
            ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.FromPath,
                nameof(c.FromPath), fallbackHint),
            StreamDataNodeHelpers.ToUtcOrNull(c.To)
            ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.ToPath,
                nameof(c.ToPath), fallbackHint),
            c.Limit ?? StreamDataNodeHelpers.ResolveIntFromPath(dataContext, nodeContext, c.LimitPath,
                nameof(c.LimitPath), fallbackHint));
    }

    /// <summary>
    /// Resolves the tenant context and its stream-data repository in one go. The tenant context is
    /// returned alongside the repository because resolution-aware selection needs the archive and
    /// rollup-archive stores off the same context.
    /// </summary>
    private async Task<(ITenantContext TenantContext, IStreamDataRepository Repository)>
        ResolveStreamDataContextAsync(INodeContext nodeContext)
    {
        var tenantId = context.TenantId;
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext, tenantId);
        return (tenantContext, repository);
    }

    /// <summary>
    /// Asks the series resolver which archive of the family — the persisted base archive plus its
    /// (transitive) rollups — should answer the window at the requested point count. Returns
    /// <c>null</c> when the tenant has no rollup store at all: there is no family to route through, so
    /// the persisted archive is queried unchanged (with a warning, since the query could have been
    /// answered from a coarser rung had one been provisioned).
    /// </summary>
    /// <remarks>
    /// The resolution zone is always UTC: the node exposes no time-zone option, so a calendar-aligned
    /// rollup (day / week / month / year) whose stored reference zone is not UTC is excluded from the
    /// ladder by the resolver's per-query zone-match rule. Fixed-size (sub-day) rungs are unaffected.
    /// </remarks>
    private static async Task<SeriesResolutionResult?> ResolveSeriesAsync(
        Enum aggregationType, string sourcePath, OctoObjectId baseArchiveRtId,
        IReadOnlyList<OctoObjectId>? rtIds, DateTime from, DateTime to, int targetPoints,
        ITenantContext tenantContext, GetQueryByIdNodeConfiguration c, INodeContext nodeContext)
    {
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore == null)
        {
            nodeContext.Warning(
                $"No rollup-archive store is available for tenant '{tenantContext.TenantId}'; querying " +
                $"the archive persisted on the query ('{baseArchiveRtId}') directly.");
            return null;
        }

        // The aggregation semantics are never guessed — they come from the query's first column, or from
        // the node's Aggregation override when set.
        var requiredAggregation = MapToRollupFunction(aggregationType);

        // Composed per tenant, exactly as the GraphQL and MCP consumers of the resolver do — the
        // service is not registered in the container anywhere in the platform.
        var resolver = new SeriesResolutionService(tenantContext.GetArchiveRuntimeStore(),
            new RollupDependencyGraph(rollupStore));

        var request = new SeriesResolutionRequest(baseArchiveRtId, TargetCkTypeId: null, from, to,
            targetPoints, requiredAggregation, sourcePath)
        {
            RtIds = rtIds
        };

        try
        {
            return await resolver.ResolveAsync(request);
        }
        catch (Exception ex)
        {
            // Business "no suitable route" outcomes are signals, not exceptions — anything thrown here
            // is a store / configuration failure and must not be swallowed.
            throw MeshAdapterPipelineExecutionException.SeriesResolutionFailed(nodeContext, c.QueryRtId, ex);
        }
    }

    /// <summary>
    /// Picks the archive the downsampling query runs against. A rollup is only accepted when it answers
    /// the query <b>identically</b> to the archive persisted on it, so executing a query through this
    /// node and executing the same query in the query editor can never disagree. Where that cannot be
    /// guaranteed the persisted archive is read instead and the reason is reported as a warning, because
    /// it is nearly always a query-definition detail the author can fix.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The storage layer bins with an interval of <c>(To - From) / Limit</c> anchored on <c>From</c>, and
    /// a windowed source only contributes a row to a bin when its whole window fits inside it. A rollup
    /// therefore reproduces the base archive's numbers exactly only when every stored window lies
    /// completely inside one bin, which needs the bin width to be a whole multiple of the rollup's bucket
    /// size <em>and</em> <c>From</c> to sit on that bucket grid. A 7-day window with 10 buckets gives
    /// 16 h 48 min bins, which is not a multiple of an hourly rollup: one hourly window per bin straddles
    /// a boundary and silently drops out (measured on real data: 8 of 168 hours, -4.1 % on the total and
    /// up to -27 % on a single bin, because the dropped hour may be a load peak). The same window with
    /// 12 buckets gives 14 h bins and matches to the last digit.
    /// </para>
    /// <para>
    /// The rollup must also have aggregated the whole window already: a watermark short of <c>To</c>
    /// means the newest bins would read low. Calendar-aligned rollups (day / week / month / year) are
    /// never accepted, because a fixed-width bin cannot line up with civil buckets in general.
    /// </para>
    /// </remarks>
    private static async Task<OctoObjectId> ResolveEffectiveArchiveAsync(SeriesResolutionResult resolution,
        OctoObjectId persistedArchiveRtId, DateTime from, DateTime to, int limit,
        ITenantContext tenantContext, INodeContext nodeContext)
    {
        if (resolution.Signal != SeriesResolutionSignal.Ok)
        {
            nodeContext.Warning(
                $"Series resolution for archive '{persistedArchiveRtId}' returned {resolution.Signal}: " +
                $"{resolution.Diagnostic ?? "no diagnostic"}");
        }

        // An empty ladder carries no archive at all; a resolver that stayed on the persisted archive needs
        // no check - that is the archive the query names.
        if (resolution.Signal == SeriesResolutionSignal.EmptyLadder ||
            resolution.ArchiveRtId == OctoObjectId.Empty ||
            resolution.ArchiveRtId == persistedArchiveRtId)
        {
            nodeContext.Info(
                $"Downsampling query reads its own archive '{persistedArchiveRtId}' (bin width " +
                $"{DescribeBinWidth(from, to, limit)}).");
            return persistedArchiveRtId;
        }

        // Reading the rollup definition can only ever make the routing better; if it fails, the archive
        // the query names is still the correct answer, so this degrades instead of failing the pipeline.
        RollupArchiveSnapshot? rollup = null;
        var rollupStore = tenantContext.GetRollupArchiveRuntimeStore();
        if (rollupStore != null)
        {
            try
            {
                rollup = await rollupStore.GetAsync(resolution.ArchiveRtId);
            }
            catch (Exception ex)
            {
                nodeContext.Warning(
                    $"Reading the rollup definition of '{resolution.ArchiveRtId}' failed ({ex.Message}); " +
                    $"querying the archive persisted on the query ('{persistedArchiveRtId}') instead.");
                return persistedArchiveRtId;
            }
        }

        if (rollup is null)
        {
            nodeContext.Warning(
                $"Series resolution picked archive '{resolution.ArchiveRtId}', but its rollup definition " +
                $"could not be read; querying the archive persisted on the query " +
                $"('{persistedArchiveRtId}') instead.");
            return persistedArchiveRtId;
        }

        var rollupName = rollup.RtWellKnownName ?? rollup.RtId.ToString();

        if (RollupAnswersExactly(rollup, from, to, limit, out var reason))
        {
            nodeContext.Info(
                $"Downsampling query routed to rollup '{rollupName}' ({rollup.RtId}) instead of " +
                $"'{persistedArchiveRtId}': bucket size {Describe(rollup.BucketSize)}, bin width " +
                $"{DescribeBinWidth(from, to, limit)}.");
            return resolution.ArchiveRtId;
        }

        nodeContext.Warning(
            $"Rollup '{rollupName}' ({rollup.RtId}) would not return the same values as the archive " +
            $"persisted on the query: {reason} Querying '{persistedArchiveRtId}' instead.");
        return persistedArchiveRtId;
    }

    /// <summary>
    /// True when reducing over <paramref name="rollup" /> yields exactly the values the base archive
    /// would. See the remarks on <see cref="ResolveEffectiveArchiveAsync" /> for the reasoning; the out
    /// parameter carries an author-facing explanation of the first condition that failed.
    /// </summary>
    private static bool RollupAnswersExactly(RollupArchiveSnapshot rollup, DateTime from, DateTime to,
        int limit, out string reason)
    {
        if (rollup.BucketAlignment != BucketAlignment.FixedSize)
        {
            reason = $"its buckets are {rollup.BucketAlignment}-aligned, which a fixed-width bin cannot " +
                     "line up with.";
            return false;
        }

        var grainTicks = rollup.BucketSize.Ticks;
        if (grainTicks <= 0)
        {
            reason = "its bucket size is not declared.";
            return false;
        }

        // The bin width the storage layer will actually use - whole seconds, rounded, never below 1 s.
        var binSeconds = EffectiveBinSeconds(from, to, limit);
        var binTicks = binSeconds * TimeSpan.TicksPerSecond;

        if (binTicks % grainTicks != 0)
        {
            reason = $"the bin width {Describe(TimeSpan.FromSeconds(binSeconds))} is not a whole multiple " +
                     $"of its {Describe(rollup.BucketSize)} bucket size, so one bucket per bin would " +
                     "straddle a bin boundary and be dropped. Choose a Limit that divides the time range " +
                     "into whole multiples of the bucket size.";
            return false;
        }

        if (from.Ticks % grainTicks != 0)
        {
            reason = $"the range start {from:O} does not sit on its {Describe(rollup.BucketSize)} bucket " +
                     "grid, so every bucket would straddle a bin boundary.";
            return false;
        }

        // A watermark short of the range end means the newest buckets are not aggregated yet.
        if (rollup.LastAggregatedBucketEnd is not { } watermark || watermark < to)
        {
            reason = "it has only aggregated up to " +
                     $"{rollup.LastAggregatedBucketEnd?.ToString("O") ?? "no bucket yet"}, which does not " +
                     $"cover the requested range end {to:O}.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    /// <summary>
    /// Bin width in whole seconds as the storage layer derives it: the range divided by the bucket count,
    /// rounded to the nearest second and never below one second.
    /// </summary>
    private static long EffectiveBinSeconds(DateTime from, DateTime to, int limit)
    {
        return Math.Max(1L,
            (long)Math.Round((to - from).TotalSeconds / limit, MidpointRounding.AwayFromZero));
    }

    private static string DescribeBinWidth(DateTime from, DateTime to, int limit)
    {
        return Describe(TimeSpan.FromSeconds(EffectiveBinSeconds(from, to, limit)));
    }

    private static string Describe(TimeSpan value)
    {
        return value.TotalDays >= 1 ? $"{value.TotalDays:0.###} d"
            : value.TotalHours >= 1 ? $"{value.TotalHours:0.###} h"
            : value.TotalMinutes >= 1 ? $"{value.TotalMinutes:0.###} min"
            : $"{value.TotalSeconds:0.###} s";
    }

    /// <summary>
    /// Maps the query's persisted <see cref="RtFieldFilterRecord"/> filters and the node's configured
    /// field filters into a single engine <see cref="FieldFilter"/> list (AND-combined). The node
    /// filters are converted by reusing the shared <see cref="FieldFilterExtensions.GetFieldFilter"/>
    /// path (which resolves <c>ComparisonValuePath</c> against the data context) via a throwaway
    /// <see cref="RtEntityQueryOptions"/> so the value-path logic is not duplicated.
    /// </summary>
    private static IReadOnlyList<FieldFilter>? BuildStreamDataFieldFilters(
        IEnumerable<RtFieldFilterRecord>? persistedFilters, IDataContext dataContext,
        GetQueryByIdNodeConfiguration c)
    {
        var filters = new List<FieldFilter>();

        if (persistedFilters != null)
        {
            filters.AddRange(persistedFilters.Select(f =>
                new FieldFilter(f.AttributePath, (FieldFilterOperator)(int)f.Operator, f.ComparisonValue)));
        }

        if (c.FieldFilters is { Count: > 0 })
        {
            var scratch = RtEntityQueryOptions.Create();
            c.FieldFilters.GetFieldFilter(dataContext, scratch);
            if (scratch.FieldFilters != null)
            {
                filters.AddRange(scratch.FieldFilters);
            }
        }

        return filters.Count == 0 ? null : filters;
    }

    private static QueryResult BuildSimpleStreamDataQueryResult(RtSimpleSdQuery query,
        StreamDataQueryResult result)
    {
        var columns = query.Columns?.ToList() ?? [];

        var queryResult = new QueryResult();

        // A simple stream-data query returns a time series: the leading Timestamp column is the time
        // axis, followed by the projected attribute columns. This differs from the runtime simple
        // query (one row per entity, no timestamp) — see result-shape mapping in the developer guide.
        queryResult.Columns.Add(new QueryResultColumns { Header = "Timestamp" });
        queryResult.Columns.AddRange(columns.Select(column => new QueryResultColumns { Header = column }));

        foreach (var row in result.Rows)
        {
            var values = new List<object?> { row.Timestamp };
            values.AddRange(columns.Select(column => StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, column)));

            queryResult.Rows.Add(new QueryResultRow
            {
                RtId = row.RtId,
                CkTypeId = row.CkTypeId,
                Values = values
            });
        }

        return queryResult;
    }


    private static QueryResult BuildAggregationStreamDataQueryResult(RtAggregationSdQuery query,
        StreamDataQueryResult result)
    {
        var columns = query.Columns.ToList();

        var queryResult = new QueryResult();
        // Parity with the runtime aggregation result: one column per aggregation, headed by the
        // attribute path, and a single row of aggregate values (RtId null).
        queryResult.Columns.AddRange(columns.Select(column =>
            new QueryResultColumns { Header = column.AttributePath }));

        var row = result.Rows.FirstOrDefault();
        var values = columns
            .Select(column => row is null
                ? null
                : StreamDataNodeHelpers.ResolveStreamAggregationValue(row.Values, column.AttributePath, column.AggregationType))
            .ToList();

        queryResult.Rows.Add(new QueryResultRow { Values = values });

        return queryResult;
    }

    private static QueryResult BuildGroupedAggregationStreamDataQueryResult(
        RtGroupingAggregationSdQuery query, List<string> groupingColumns, StreamDataQueryResult result)
    {
        var aggregationColumns = query.Columns.ToList();

        var queryResult = new QueryResult();
        // Parity with the runtime grouped aggregation: group-by columns first, then the aggregation
        // columns; one row per group (RtId null).
        queryResult.Columns.AddRange(groupingColumns.Select(col =>
            new QueryResultColumns { Header = col }));
        queryResult.Columns.AddRange(aggregationColumns.Select(column =>
            new QueryResultColumns { Header = column.AttributePath }));

        foreach (var row in result.Rows)
        {
            var values = new List<object?>();
            // Group-key columns are keyed by their physical column name, same as simple projections.
            values.AddRange(groupingColumns.Select(col => StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, col)));
            values.AddRange(aggregationColumns.Select(column =>
                StreamDataNodeHelpers.ResolveStreamAggregationValue(row.Values, column.AttributePath, column.AggregationType)));

            queryResult.Rows.Add(new QueryResultRow { Values = values });
        }

        return queryResult;
    }

    private static IReadOnlyList<AggregationColumn> BuildStreamAggregationColumns(
        IEnumerable<RtAggregationQueryColumnRecord>? columns)
    {
        return columns?
            .Select(col => new AggregationColumn(col.AttributePath, StreamDataNodeHelpers.MapStreamAggregation(col.AggregationType).Function))
            .ToList() ?? [];
    }

    /// <summary>
    /// One downsampling output column: the header the result keeps (the persisted attribute path), the
    /// engine aggregation to request, and the lower-case token the storage layer suffixes the output
    /// key with (<c>{physicalColumn}_{token}</c>).
    /// </summary>
    private readonly record struct DownsamplingColumn(
        string AttributePath, AggregationFunction Function, string KeyToken);

    /// <summary>
    /// Maps the persisted aggregation columns onto the engine's. The node's optional <c>Aggregation</c>
    /// override, when set, replaces the persisted function on <b>every</b> column — a single option
    /// cannot express a per-column choice, and the archive selection matched a rollup on that one
    /// function, so every value read back should come from it. Not set means each column keeps the
    /// aggregation persisted on the query.
    /// </summary>
    private static List<DownsamplingColumn> BuildDownsamplingColumns(
        IReadOnlyList<RtAggregationQueryColumnRecord> persistedColumns, Enum? aggregationOverride)
    {
        var result = new List<DownsamplingColumn>(persistedColumns.Count);

        foreach (var column in persistedColumns)
        {
            var (function, keyToken) = StreamDataNodeHelpers.MapStreamAggregation(aggregationOverride ?? column.AggregationType);
            result.Add(new DownsamplingColumn(column.AttributePath, function, keyToken));
        }

        return result;
    }

    /// <summary>
    /// Maps the node's optional aggregation override onto the persisted aggregation-type enum, so the
    /// override flows through exactly the same mapping as a value read from the query entity. The
    /// override mirrors the aggregations a query definition can carry (Count, Minimum, Maximum, Average,
    /// Sum); the remaining enum members have no downsampling counterpart here — a time-weighted average
    /// and a state duration need per-column metadata (carry lookback, comparison value) the node cannot
    /// supply — and are rejected with an actionable error rather than a mapping crash.
    /// </summary>
    private static Enum? MapAggregationOverride(AggregationTypesDto? aggregation, INodeContext nodeContext)
    {
        return aggregation switch
        {
            null => null,
            AggregationTypesDto.Count => RtAggregationTypesEnum.Count,
            AggregationTypesDto.Minimum => RtAggregationTypesEnum.Minimum,
            AggregationTypesDto.Maximum => RtAggregationTypesEnum.Maximum,
            AggregationTypesDto.Average => RtAggregationTypesEnum.Average,
            AggregationTypesDto.Sum => RtAggregationTypesEnum.Sum,
            _ => throw MeshAdapterPipelineExecutionException.UnsupportedAggregationType(
                nodeContext, aggregation.Value.ToString())
        };
    }

    /// <summary>
    /// Maps an aggregation type — persisted on the query column or supplied as the node's override —
    /// onto the rollup function the resolver matches ladder rungs against. Only the aggregations a query
    /// definition can carry are expressible; anything else is rejected earlier by
    /// <see cref="MapAggregationOverride" /> (override) or by
    /// <see cref="StreamDataNodeHelpers.MapStreamAggregation" /> (persisted column).
    /// </summary>
    private static CkRollupFunction MapToRollupFunction(Enum aggregationType)
    {
        return aggregationType.ToString() switch
        {
            "Count" => CkRollupFunction.Count,
            "Sum" => CkRollupFunction.Sum,
            "Average" => CkRollupFunction.Avg,
            "Minimum" => CkRollupFunction.Min,
            "Maximum" => CkRollupFunction.Max,
            _ => throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType,
                $"Unknown aggregation type: {aggregationType}")
        };
    }

    /// <summary>
    /// Maps the downsampling rows into a <see cref="QueryResult" />: a leading <c>Timestamp</c> column
    /// (the bin start, as for a simple stream-data query), then one column per aggregation headed by its
    /// attribute path. One row per bin; empty bins keep their timestamp and carry null aggregates.
    /// <c>Skip</c>/<c>Take</c> page the returned bins in memory — the storage layer's downsampling path
    /// ignores offset / page size because the bin axis is generated, not paged, and the row count is
    /// governed by the bucket count.
    /// </summary>
    private static QueryResult BuildDownsamplingStreamDataQueryResult(List<DownsamplingColumn> columns,
        StreamDataQueryResult result, GetQueryByIdNodeConfiguration c)
    {
        var queryResult = new QueryResult();
        queryResult.Columns.Add(new QueryResultColumns { Header = "Timestamp" });
        queryResult.Columns.AddRange(columns.Select(col =>
            new QueryResultColumns { Header = col.AttributePath }));

        IEnumerable<StreamDataRow> rows = result.Rows;
        if (c.Skip.HasValue)
        {
            rows = rows.Skip(c.Skip.Value);
        }

        if (c.Take.HasValue)
        {
            rows = rows.Take(c.Take.Value);
        }

        foreach (var row in rows)
        {
            var values = new List<object?> { row.Timestamp };
            values.AddRange(columns.Select(col => ResolveDownsamplingValue(row.Values, col)));

            queryResult.Rows.Add(new QueryResultRow
            {
                // The store stamps the query's CkTypeId on every bin row, empty or not; RtId is only
                // populated when the projection carries a single source entity.
                RtId = row.RtId,
                CkTypeId = row.CkTypeId,
                Values = values
            });
        }

        return queryResult;
    }

    /// <summary>
    /// Reads one bin's aggregate. The downsampling path keys results only by the friendly output name
    /// <c>{physicalColumn}_{token}</c> — both for plain columns and for the rollup-chain resolved form,
    /// whose SQL alias is normalised through the same column-name rule — so no SQL-alias fallback is
    /// needed here.
    /// </summary>
    private static object? ResolveDownsamplingValue(IReadOnlyDictionary<string, object?> values,
        DownsamplingColumn column)
    {
        var physicalColumnName = column.AttributePath.Replace(".", string.Empty).ToLowerInvariant();
        return values.TryGetValue($"{physicalColumnName}_{column.KeyToken}", out var value) ? value : null;
    }
}
