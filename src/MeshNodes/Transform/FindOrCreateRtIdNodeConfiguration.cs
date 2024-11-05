using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for apply changes to the object in mongodb
/// </summary>
[NodeName("FindOrCreateRtId", 1)]
public record FindOrCreateRtIdNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// The CkTypeId of the object
    /// </summary>
    public required CkId<CkTypeId>? CkTypeId { get; set; }
    
    /// <summary>
    /// the field filters
    /// </summary>
    public required ICollection<PathFieldFilter>? FieldFilters { get; set; }
    
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

/// <summary>
/// Field filter that uses the json path to the value
/// </summary>
public record PathFieldFilter
{
    /// <summary>
    /// The Path to the value
    /// </summary>
    public required string Path { get; set; }
    
    /// <summary>
    /// The attribute name
    /// </summary>
    public required string AttributeName { get; set; }
    
    /// <summary>
    /// The Operator
    /// </summary>
    public required FieldFilterOperator Operator { get; set; }
}
