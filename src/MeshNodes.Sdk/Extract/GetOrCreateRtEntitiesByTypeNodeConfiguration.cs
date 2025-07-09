using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node get rt entities by type including field filters
/// </summary>
[NodeName("GetOrCreateRtEntitiesByType", 1)]
public record GetOrCreateRtEntitiesByTypeNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// The CkTypeId of the object
    /// </summary>
    public required CkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// Gets or sets the json path to the CkTypeId
    /// </summary>
    public string? CkTypeIdPath { get; set; }
    
    /// <summary>
    /// the field filters
    /// </summary>
    public required ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }
    
    /// <summary>
    /// Target path where the RtId should be written
    /// </summary>
    public string RtIdTargetPath { get; set; } = "$.rtId";
    
    /// <summary>
    /// Target path where the CkTypeId should be written
    /// </summary>
    public string CkTypeIdTargetPath { get; set; } = "$.ckTypeId";
    
    /// <summary>
    /// 
    /// </summary>
    public string ModOperationPath { get; set; } = "$.modOperation";
}

