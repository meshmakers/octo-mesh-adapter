using Meshmakers.Octo.MeshNodes.Nodes;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByTypeNodeConfiguration))]
public class GetRtEntitiesByTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
       var c = dataContext.GetNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), "CkTypeId is not set");
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

        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(etlContext.Session, c.CkTypeId, dataQueryOperation, c.Skip, c.Take);

        dataContext.SetCurrentValueByPath(c.TargetPropertyName, r);
        
        
        await next(dataContext);
    }
}