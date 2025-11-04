using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Node get query by id
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
            await context.TenantRepository.GetRtEntityByRtIdAsync<RtQuery>(
                session, c.QueryRtId);

        if (rtQuery == null)
        {
            nodeContext.Error("Query '{0}' not found", c.QueryRtId);
            return;
        }

        var queryOptions = RtEntityQueryOptions.Create();
        if (rtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtQuery.FieldFilter)
            {
                queryOptions.AddFieldFilter(fieldFilter.AttributePath, (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        // Add field filters from the configuration
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);

        if (rtQuery.Sorting != null)
        {
            foreach (var orderItemRecord in rtQuery.Sorting)
            {
                queryOptions.SortOrder(orderItemRecord.AttributePath, (SortOrders)orderItemRecord.SortOrder);
            }
        }

        if (rtQuery.AttributeSearchFilter != null)
        {
            queryOptions.AttributeSearch(rtQuery.AttributeSearchFilter.AttributePaths,
                rtQuery.AttributeSearchFilter.SearchValue);
        }

        if (rtQuery.TextSearchFilter != null)
        {
            queryOptions.TextSearch(rtQuery.TextSearchFilter.SearchValue);
        }

        var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService,
            context.TenantRepository.TenantId, rtQuery.QueryCkTypeId,
            rtQuery.Columns);

        var resultSet = await context.TenantRepository.GetRtEntitiesGraphByTypeAsync(session, rtQuery.QueryCkTypeId,
            queryOptions, roleIdDirectionPairs, c.Skip, c.Take);

        await session.CommitTransactionAsync();

        QueryResult queryResult = new();
        queryResult.Columns.AddRange(rtQuery.Columns.Select(column => new QueryResultColumns
            { Header = column }));
        queryResult.Rows.AddRange(resultSet.Items.Select(entity => new QueryResultRow
        {
            RtId = entity.RtId,
            CkTypeId = entity.CkTypeId ?? throw new Exception("CkTypeId is null"),
            Values = rtQuery.Columns.Select(column =>
                entity.GetAttributeValueByAccessPath(ckCacheService, context.TenantId, column)).ToList()
        }));

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode,
            queryResult);


        await next(dataContext, nodeContext);
    }


}