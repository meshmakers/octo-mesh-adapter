using Meshmakers.Common.Shared;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v1;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

/// <summary>
/// Node get query by id
/// </summary>
/// <param name="next"></param>
/// <param name="context"></param>
[NodeConfiguration(typeof(GetQueryByIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetQueryByIdNode(NodeDelegate next, IMeshEtlContext context) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<GetQueryByIdNodeConfiguration>();
        
        var session = await context.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        
        var rtQuery =
            await context.TenantRepository.GetRtEntityByRtIdAsync<RtQuery>(
                session, c.QueryRtId);
        
        if (rtQuery == null)
        {
            dataContext.NodeContext.Error("Query '{0}' not found", c.QueryRtId);
            return;
        }
        
        var dataQueryOperation = DataQueryOperation.Create();
        if (rtQuery.FieldFilter != null)
        {
            foreach (var fieldFilter in rtQuery.FieldFilter)
            {
                dataQueryOperation.AddFieldFilter(fieldFilter.AttributePath, (FieldFilterOperator) fieldFilter.Operator, fieldFilter.ComparisonValue);
            }
        }

        if (rtQuery.Sorting != null)
        {
            foreach (var orderItemRecord in rtQuery.Sorting)
            {
                dataQueryOperation.SortOrder(orderItemRecord.AttributePath, (SortOrders)orderItemRecord.SortOrder);
            }
        }

        if (rtQuery.AttributeSearchFilter != null)
        {
            dataQueryOperation.AttributeSearch(rtQuery.AttributeSearchFilter.AttributePaths, rtQuery.AttributeSearchFilter.SearchValue);
        }
        
        if (rtQuery.TextSearchFilter != null)
        {
            dataQueryOperation.TextSearch(rtQuery.TextSearchFilter.SearchValue);
        }
        
        var resultSet = await context.TenantRepository.GetRtEntitiesByTypeAsync(session, rtQuery.QueryCkTypeId, dataQueryOperation, c.Skip, c.Take);
        
        await session.CommitTransactionAsync();
        
        QueryResult queryResult = new();
        queryResult.Columns.AddRange(rtQuery.Columns.Select(column => new QueryResultColumns { Header = column.ToPascalCase() }));
        queryResult.Rows.AddRange(resultSet.Items.Select(entity => new QueryResultRow
        {
            RtId = entity.RtId,
            CkTypeId = entity.CkTypeId ?? throw new Exception("CkTypeId is null"),
            Values = rtQuery.Columns.Select(column => entity.GetAttributeValueOrDefault(column.ToPascalCase())).ToList()
        }));
        
        dataContext.SetValueByPath(c.TargetPath, c.TargetValueKind, c.TargetValueWriteMode, queryResult);


        await next(dataContext);

    }
}