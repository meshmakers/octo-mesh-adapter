using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal static class SimpleRtQueryExecution
{
    public static async Task<IReadOnlyList<OctoObjectId>> FindRtIdsAsync(IMeshEtlContext context,
        ICkCacheService ckCacheService, IOctoSession session, RtSimpleRtQuery simpleQuery, int take)
    {
        var queryOptions = RtEntityQueryOptions.Create().WithCachingDisabled();
        ConfigureOptions(simpleQuery, queryOptions);

        var filterNavigation = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
            context.TenantRepository.TenantId, simpleQuery.QueryCkTypeId, [],
            queryOptions.FieldFilters ?? new List<FieldFilter>());

        var resultSet = await context.TenantRepository.GetRtEntitiesGraphByTypeAsync(session,
            simpleQuery.QueryCkTypeId, queryOptions, filterNavigation, null, take);

        return resultSet.Items.Select(entity => entity.RtId).Distinct().ToList();
    }

    public static async Task<QueryResult> RunForRtIdsAsync(IMeshEtlContext context, ICkCacheService ckCacheService,
        IOctoSession session, RtSimpleRtQuery simpleQuery, IReadOnlyList<OctoObjectId> rtIds)
    {
        var columnNavigation = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
            context.TenantRepository.TenantId, simpleQuery.QueryCkTypeId, simpleQuery.Columns);

        var resultSet = await context.TenantRepository.GetRtEntitiesGraphByIdAsync(session,
            simpleQuery.QueryCkTypeId, rtIds, RtEntityQueryOptions.Create().WithCachingDisabled(), columnNavigation);

        var queryResult = new QueryResult();
        BuildResult(ckCacheService, context.TenantId, simpleQuery, resultSet, queryResult);
        var order = rtIds.Select((rtId, index) => (rtId, index)).ToDictionary(x => x.rtId, x => x.index);
        queryResult.Rows.Sort((a, b) => Position(a).CompareTo(Position(b)));
        return queryResult;

        int Position(QueryResultRow row) =>
            row.RtId is { } id && order.TryGetValue(id, out var index) ? index : int.MaxValue;
    }

    public static void ConfigureOptions(RtSimpleRtQuery simpleQuery, RtEntityQueryOptions queryOptions)
    {
        if (simpleQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in simpleQuery.FieldFilter)
            {
                queryOptions.AddFieldFilter(fieldFilter.AttributePath,
                    FieldFilterOperatorDtoExtensions.FromCkModelEnum(fieldFilter.Operator),
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

    public static void BuildResult(ICkCacheService ckCacheService, string tenantId, RtSimpleRtQuery simpleQuery,
        IResultSet<RtEntityGraphItem> resultSet, QueryResult queryResult)
    {
        queryResult.Columns.AddRange(simpleQuery.Columns.Select(column => new QueryResultColumns
            { Header = column }));
        queryResult.Rows.AddRange(resultSet.Items.Select(entity => new QueryResultRow
        {
            RtId = entity.RtId,
            CkTypeId = entity.CkTypeId ?? throw new Exception("CkTypeId is null"),
            Values = simpleQuery.Columns.Select(column =>
                entity.GetAttributeValueByAccessPath(ckCacheService, tenantId, column)).ToList()
        }));
    }
}
