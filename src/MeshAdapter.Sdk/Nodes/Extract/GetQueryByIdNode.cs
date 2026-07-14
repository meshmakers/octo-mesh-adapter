using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Node get query by id. Supports runtime-data queries (simple, aggregation, grouped aggregation)
/// and simple stream-data queries. The caller does not need to know the query kind in advance — the
/// persisted query entity (a shared <see cref="RtPersistentQuery"/> subtype) is resolved and
/// dispatched based on its concrete type.
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

        var session = await context.TenantRepository.GetSessionAsync();
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
        var streamDataRepo = await ResolveStreamDataRepositoryAsync(nodeContext);

        if (string.IsNullOrWhiteSpace(query.ArchiveRtId))
        {
            throw MeshAdapterPipelineExecutionException.ArchiveRtIdMissing(nodeContext, c.QueryRtId);
        }

        var archiveRtId = new OctoObjectId(query.ArchiveRtId);

        var queryResult = query switch
        {
            RtSimpleSdQuery simple =>
                await ExecuteSimpleStreamDataQueryAsync(simple, archiveRtId, streamDataRepo, dataContext, c,
                    nodeContext),
            RtAggregationSdQuery aggregation =>
                await ExecuteAggregationStreamDataQueryAsync(aggregation, archiveRtId, streamDataRepo, dataContext,
                    c, nodeContext),
            RtGroupingAggregationSdQuery grouped =>
                await ExecuteGroupedAggregationStreamDataQueryAsync(grouped, archiveRtId, streamDataRepo,
                    dataContext, c, nodeContext),
            // Downsampling and any future stream-data query types are not yet supported.
            _ => throw MeshAdapterPipelineExecutionException.UnsupportedQueryType(nodeContext,
                query.GetType().Name)
        };

        dataContext.Set(c.TargetPath, queryResult, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
    }

    private async Task<QueryResult> ExecuteSimpleStreamDataQueryAsync(RtSimpleSdQuery query,
        OctoObjectId archiveRtId, IStreamDataRepository streamDataRepo, IDataContext dataContext,
        GetQueryByIdNodeConfiguration c, INodeContext nodeContext)
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
            .WithTimeRange(c.From ?? query.From, c.To ?? query.To)
            .WithLimit(c.Limit ?? (query.Limit.HasValue ? (int)query.Limit.Value : null))
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
        GetQueryByIdNodeConfiguration c, INodeContext nodeContext)
    {
        var rtIds = query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataAggregationQueryOptions.Create()
            .WithCkTypeId(query.QueryCkTypeId)
            .WithAggregationColumns(BuildStreamAggregationColumns(query.Columns))
            .WithRtIds(rtIds)
            .WithTimeRange(c.From ?? query.From, c.To ?? query.To)
            .WithFieldFilters(BuildStreamDataFieldFilters(query.FieldFilter, dataContext, c));

        var result = await ExecuteAsync(
            () => streamDataRepo.ExecuteAggregationQueryAsync(archiveRtId, options), nodeContext, c);

        return BuildAggregationStreamDataQueryResult(query, result);
    }

    private async Task<QueryResult> ExecuteGroupedAggregationStreamDataQueryAsync(
        RtGroupingAggregationSdQuery query, OctoObjectId archiveRtId, IStreamDataRepository streamDataRepo,
        IDataContext dataContext, GetQueryByIdNodeConfiguration c, INodeContext nodeContext)
    {
        var groupingColumns = query.GroupingColumns?.ToList() ?? [];
        var rtIds = query.RtIds?.Select(id => new OctoObjectId(id)).ToList();

        var options = StreamDataGroupedAggregationQueryOptions.Create()
            .WithCkTypeId(query.QueryCkTypeId)
            .WithGroupByColumns(groupingColumns)
            .WithAggregationColumns(BuildStreamAggregationColumns(query.Columns))
            .WithRtIds(rtIds)
            .WithTimeRange(c.From ?? query.From, c.To ?? query.To)
            .WithFieldFilters(BuildStreamDataFieldFilters(query.FieldFilter, dataContext, c));

        var result = await ExecuteAsync(
            () => streamDataRepo.ExecuteGroupedAggregationQueryAsync(archiveRtId, options), nodeContext, c);

        return BuildGroupedAggregationStreamDataQueryResult(query, groupingColumns, result);
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

    private async Task<IStreamDataRepository> ResolveStreamDataRepositoryAsync(INodeContext nodeContext)
    {
        var tenantId = context.TenantId;
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
        return tenantContext.GetStreamDataRepository()
            ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext, tenantId);
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
            values.AddRange(columns.Select(column => ResolveStreamColumnValue(row.Values, column)));

            queryResult.Rows.Add(new QueryResultRow
            {
                RtId = row.RtId,
                CkTypeId = row.CkTypeId,
                Values = values
            });
        }

        return queryResult;
    }

    /// <summary>
    /// Resolves a projected column value from a <see cref="StreamDataRow"/>. The stream-data store
    /// keys <see cref="StreamDataRow.Values"/> by the physical CrateDB column name — the attribute
    /// path stripped of its dot separators and lower-cased (see the storage layer's
    /// <c>ColumnNameMapper.PathToColumnName</c>). Standard columns such as <c>window_start</c> or
    /// <c>was_updated</c> already equal their physical name and match directly; dotted / mixed-case
    /// attribute paths such as <c>amount.value</c> or <c>obisCode</c> only match after normalisation.
    /// Tries the exact key first (cheap, covers the standard columns) and falls back to the
    /// normalised form.
    /// </summary>
    private static object? ResolveStreamColumnValue(IReadOnlyDictionary<string, object?> values,
        string attributePath)
    {
        if (values.TryGetValue(attributePath, out var direct))
        {
            return direct;
        }

        var physicalColumnName = attributePath.Replace(".", string.Empty).ToLowerInvariant();
        return values.TryGetValue(physicalColumnName, out var mapped) ? mapped : null;
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
                : ResolveStreamAggregationValue(row.Values, column.AttributePath, column.AggregationType))
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
            values.AddRange(groupingColumns.Select(col => ResolveStreamColumnValue(row.Values, col)));
            values.AddRange(aggregationColumns.Select(column =>
                ResolveStreamAggregationValue(row.Values, column.AttributePath, column.AggregationType)));

            queryResult.Rows.Add(new QueryResultRow { Values = values });
        }

        return queryResult;
    }

    private static IReadOnlyList<AggregationColumn> BuildStreamAggregationColumns(
        IEnumerable<RtAggregationQueryColumnRecord>? columns)
    {
        return columns?
            .Select(col => new AggregationColumn(col.AttributePath, MapStreamAggregation(col.AggregationType).Function))
            .ToList() ?? [];
    }

    /// <summary>
    /// Resolves an aggregation value from a <see cref="StreamDataRow"/>. The stream-data store keys
    /// aggregate results by the friendly output name <c>{physicalColumn}_{funcToken}</c> (e.g.
    /// <c>amountvalue_avg</c>) — the attribute path stripped of dots and lower-cased, suffixed with
    /// the lower-case function token. Falls back to the SQL-alias form <c>{Func}_{physicalColumn}</c>
    /// (e.g. <c>Avg_amountvalue</c>) that the store also surfaces.
    /// </summary>
    private static object? ResolveStreamAggregationValue(IReadOnlyDictionary<string, object?> values,
        string attributePath, Enum aggregationType)
    {
        var token = MapStreamAggregation(aggregationType).KeyToken;
        var column = attributePath.Replace(".", string.Empty).ToLowerInvariant();

        var outputName = $"{column}_{token}";
        if (values.TryGetValue(outputName, out var v))
        {
            return v;
        }

        var sqlAlias = $"{char.ToUpperInvariant(token[0])}{token[1..]}_{column}";
        return values.TryGetValue(sqlAlias, out var v2) ? v2 : null;
    }

    /// <summary>
    /// Maps the persisted aggregation-type enum to the engine <see cref="AggregationFunction"/> (used
    /// to build the query options) and the lower-case result-key token the storage layer uses when
    /// naming the aggregate output column.
    /// </summary>
    private static (AggregationFunction Function, string KeyToken) MapStreamAggregation(Enum aggregationType)
    {
        return aggregationType.ToString() switch
        {
            "Count" => (AggregationFunction.Count, "count"),
            "Sum" => (AggregationFunction.Sum, "sum"),
            "Average" => (AggregationFunction.Average, "avg"),
            "Minimum" => (AggregationFunction.Minimum, "min"),
            "Maximum" => (AggregationFunction.Maximum, "max"),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType,
                $"Unknown aggregation type: {aggregationType}")
        };
    }
}
