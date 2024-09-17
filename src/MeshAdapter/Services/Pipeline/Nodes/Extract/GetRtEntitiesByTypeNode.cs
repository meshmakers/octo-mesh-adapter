using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByTypeNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
       var c = dataContext.NodeContext.GetNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.NodeContext.Error("CkTypeId is not set");
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

        dataContext.SetValueByPath(c.TargetPath, c.TargetValueKind, c.TargetValueWriteMode, r);
        
        
        await next(dataContext);
    }
}