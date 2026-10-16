using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

internal sealed record NotificationCandidate(OctoObjectId RtId, DateTime ChangedAt);

internal static class SimpleRtQueryExecution
{
    private const string ChangedAtPath = "rtChangedDateTime";

    private const string RtIdPath = "rtId";

    public static bool FiltersThroughAssociations(RtSimpleRtQuery simpleQuery) =>
        simpleQuery.FieldFilter?.Any(filter =>
            filter.AttributePath.Contains("->", StringComparison.Ordinal) ||
            filter.AttributePath.Contains("::", StringComparison.Ordinal)) == true;

    public static async Task<IReadOnlyList<NotificationCandidate>> FindChangedAsync(ITenantRepository repository,
        ICkCacheService ckCacheService, IOctoSession session, RtSimpleRtQuery simpleQuery, DateTime? changedAfter,
        IReadOnlyCollection<OctoObjectId> excludedRtIds, int take)
    {
        var queryOptions = RtEntityQueryOptions.Create().WithCachingDisabled();
        ConfigureFilters(simpleQuery, queryOptions);
        if (changedAfter is { } after)
        {
            queryOptions.AddFieldFilter(ChangedAtPath, FieldFilterOperator.GreaterThan, after);
        }

        if (excludedRtIds.Count > 0)
        {
            queryOptions.AddFieldFilter(RtIdPath, FieldFilterOperator.NotIn, excludedRtIds.ToList());
        }

        queryOptions.SortOrder(ChangedAtPath, SortOrders.Ascending);
        queryOptions.SortOrder(RtIdPath, SortOrders.Ascending);

        var filterNavigation = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
            repository.TenantId, simpleQuery.QueryCkTypeId, [], queryOptions.FieldFilters ?? new List<FieldFilter>());

        var resultSet = await repository.GetRtEntitiesGraphByTypeAsync(session, simpleQuery.QueryCkTypeId,
            queryOptions, filterNavigation, null, take);

        return resultSet.Items
            .DistinctBy(entity => entity.RtId)
            .Select(entity => new NotificationCandidate(entity.RtId, entity.RtChangedDateTime ?? DateTime.MinValue))
            .ToList();
    }

    public static async Task<QueryResult> RunForRtIdsAsync(IMeshEtlContext context, ICkCacheService ckCacheService,
        IOctoSession session, RtSimpleRtQuery simpleQuery, IReadOnlyList<OctoObjectId> rtIds)
    {
        var queryResult = new QueryResult();
        if (simpleQuery.Sorting?.Any() == true)
        {
            var sortedOptions = RtEntityQueryOptions.Create().WithCachingDisabled();
            ConfigureOptions(simpleQuery, sortedOptions);
            sortedOptions.AddFieldFilter(RtIdPath, FieldFilterOperator.In, rtIds.ToList());
            var navigation = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
                context.TenantRepository.TenantId, simpleQuery.QueryCkTypeId, simpleQuery.Columns,
                sortedOptions.FieldFilters ?? new List<FieldFilter>());
            var sorted = await context.TenantRepository.GetRtEntitiesGraphByTypeAsync(session,
                simpleQuery.QueryCkTypeId, sortedOptions, navigation, null, rtIds.Count);
            BuildResult(ckCacheService, context.TenantId, simpleQuery, sorted, queryResult);
            return queryResult;
        }

        var columnNavigation = RtPathEvaluator.TokenizeAndGetNavigationPairsByRtCkId(ckCacheService,
            context.TenantRepository.TenantId, simpleQuery.QueryCkTypeId, simpleQuery.Columns);

        var resultSet = await context.TenantRepository.GetRtEntitiesGraphByIdAsync(session,
            simpleQuery.QueryCkTypeId, rtIds, RtEntityQueryOptions.Create().WithCachingDisabled(), columnNavigation);

        BuildResult(ckCacheService, context.TenantId, simpleQuery, resultSet, queryResult);
        var order = rtIds.Select((rtId, index) => (rtId, index)).ToDictionary(x => x.rtId, x => x.index);
        queryResult.Rows.Sort((a, b) => Position(a).CompareTo(Position(b)));
        return queryResult;

        int Position(QueryResultRow row) =>
            row.RtId is { } id && order.TryGetValue(id, out var index) ? index : int.MaxValue;
    }

    public static void ConfigureOptions(RtSimpleRtQuery simpleQuery, RtEntityQueryOptions queryOptions)
    {
        ConfigureFilters(simpleQuery, queryOptions);

        if (simpleQuery.Sorting != null)
        {
            foreach (var orderItemRecord in simpleQuery.Sorting)
            {
                queryOptions.SortOrder(orderItemRecord.AttributePath, (SortOrders)orderItemRecord.SortOrder);
            }
        }
    }

    private static void ConfigureFilters(RtSimpleRtQuery simpleQuery, RtEntityQueryOptions queryOptions)
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
