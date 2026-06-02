using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Node get query by id. Supports simple queries, aggregation queries, and grouped aggregation queries.
/// </summary>
/// <param name="next">Next node delegate in the pipeline</param>
/// <param name="context">Mesh ETL context</param>
/// <param name="ckCacheService">Construction Kit cache service</param>
[NodeConfiguration(typeof(GetQueryByIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetQueryByIdNode(NodeDelegate next, IMeshEtlContext context, ICkCacheService ckCacheService)
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
}
