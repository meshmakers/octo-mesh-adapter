using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

/// <summary>
/// Configuration for node get rt entities by type
/// </summary>
public class GetRtEntitiesByTypeNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Gets or sets the target property name
    /// </summary>
    public string? TargetPropertyName { get; set; }
    
    /// <summary>
    /// CkTypeId of query
    /// </summary>
    public CkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// Amount of items to skip
    /// </summary>
    public int? Skip { get; set; }
    
    /// <summary>
    /// Amount of items to take
    /// </summary>
    public int? Take { get; set; }
    
    /// <summary>
    /// A list of field filters
    /// </summary>
    public ICollection<FieldFilter>? FieldFilters { get; set; }
}

/// <summary>
/// Gets rt entities by type
/// </summary>
[Node("GetRtEntitiesByType", 1, typeof(GetRtEntitiesByTypeNodeConfiguration))]
public class GetRtEntitiesByTypeNode(NodeDelegate next, IRetrieverEtlContext etlContext) : IPipelineNode
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