using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
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

        var simpleQuery =
            await context.TenantRepository.GetRtEntityByRtIdAsync<RtSimpleRtQuery>(
                session, c.QueryRtId);

        if (simpleQuery == null)
        {
            nodeContext.Error("Query '{0}' not found", c.QueryRtId);
            return;
        }

        var queryOptions = RtEntityQueryOptions.Create();
        if (simpleQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in simpleQuery.FieldFilter)
            {
                queryOptions.AddFieldFilter(fieldFilter.AttributePath, (FieldFilterOperator)fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        // Add field filters from the configuration
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);

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

        var roleIdDirectionPairs = RtPathEvaluator.TokenizeAndGetNavigationPairs(ckCacheService,
            context.TenantRepository.TenantId, simpleQuery.QueryCkTypeId,
            simpleQuery.Columns);

        var resultSet = await context.TenantRepository.GetRtEntitiesGraphByTypeAsync(session, simpleQuery.QueryCkTypeId,
            queryOptions, roleIdDirectionPairs, c.Skip, c.Take);

        await session.CommitTransactionAsync();

        QueryResult queryResult = new();
        queryResult.Columns.AddRange(simpleQuery.Columns.Select(column => new QueryResultColumns
            { Header = column }));
        queryResult.Rows.AddRange(resultSet.Items.Select(entity => new QueryResultRow
        {
            RtId = entity.RtId,
            CkTypeId = entity.CkTypeId ?? throw new Exception("CkTypeId is null"),
            Values = simpleQuery.Columns.Select(column =>
                entity.GetAttributeValueByAccessPath(ckCacheService, context.TenantId, column)).ToList()
        }));

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode,
            queryResult);


        await next(dataContext, nodeContext);
    }


}