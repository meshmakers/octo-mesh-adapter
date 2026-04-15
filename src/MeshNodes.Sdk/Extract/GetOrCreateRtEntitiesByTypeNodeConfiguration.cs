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
    /// Runtime construction kit type id to filter by (use either CkTypeId or CkTypeIdPath)
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public RtCkId<CkTypeId>? CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the json path to the CkTypeId
    /// </summary>
    [PropertyGroup("Entity", 1, "jsonpath")]
    public string? CkTypeIdPath { get; set; }

    /// <summary>
    /// the field filters
    /// </summary>
    [PropertyGroup("Query", 0)]
    public required ICollection<FieldFilterWithPathDto>? FieldFilters { get; set; }

    /// <summary>
    /// Target path where the RtId should be written
    /// </summary>
    [PropertyGroup("Paths", 0, "jsonpath")]
    public string RtIdTargetPath { get; set; } = "$.rtId";

    /// <summary>
    /// Target path where the CkTypeId should be written
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string CkTypeIdTargetPath { get; set; } = "$.ckTypeId";

    /// <summary>
    ///
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public string ModOperationPath { get; set; } = "$.modOperation";
}

