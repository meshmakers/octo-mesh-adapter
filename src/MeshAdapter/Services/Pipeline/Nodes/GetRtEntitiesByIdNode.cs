using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByIdNodeConfiguration))]
public class GetRtEntitiesByIdNode(NodeDelegate next, IMeshEtlContext context) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var etlContext = context;

        var c = dataContext.GetNodeConfiguration<GetRtEntitiesByIdNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "CkTypeId is not set");
            return;
        }

        if (c.RtIds == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "RtIds is not set");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        if (c.FieldFilters != null)
        {
            foreach (var fieldFilter in c.FieldFilters)
            {
                dataQueryOperation.AddFieldFilter(fieldFilter.AttributeName, fieldFilter.Operator, fieldFilter.ComparisonValue);
            }
        }

        var r = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(etlContext.Session, c.CkTypeId, c.RtIds.ToList(), dataQueryOperation, c.Skip, c.Take);

        dataContext.SetCurrentValueByPath(c.TargetPropertyName, r);
        
        await next(dataContext);
    }
}