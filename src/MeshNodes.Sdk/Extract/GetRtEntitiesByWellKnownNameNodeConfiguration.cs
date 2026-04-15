using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for node to get RtEntities by well known name
/// </summary>
[NodeName("GetRtEntitiesByWellKnownName", 1)]
public record GetRtEntitiesByWellKnownNameNodeConfiguration : PathNodeConfiguration
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
    /// Amount of items to skip
    /// </summary>
    [PropertyGroup("Query", 0)]
    public int? Skip { get; set; }

    /// <summary>
    /// Amount of items to take
    /// </summary>
    [PropertyGroup("Query", 1)]
    public int? Take { get; set; }

    /// <summary>
    /// Path to the well known name
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public required string WellKnownNamePath { get; set; }

    /// <summary>
    /// Target path where the RtId should be written
    /// </summary>
    [PropertyGroup("Paths", 2, "jsonpath")]
    public required string RtIdTargetPath { get; set; } = "$.rtId";

    /// <summary>
    /// Target path where the CkTypeId should be written
    /// </summary>
    [PropertyGroup("Paths", 3, "jsonpath")]
    public required string CkTypeIdTargetPath { get; set; } = "$.ckTypeId";

    /// <summary>
    /// Operation path where the mod operation should be written
    /// </summary>
    [PropertyGroup("Paths", 4, "jsonpath")]
    public required string ModOperationPath { get; set; } = "$.modOperation";

    /// <summary>
    /// If true, the mod operation will be generated if the RtEntity is not found in the database
    /// </summary>
    [PropertyGroup("Options", 0)]
    public bool GenerateInsertOperation { get; set; } = false;

    /// <summary>
    /// Optional target path where the entity attributes should be written as a dictionary
    /// </summary>
    [PropertyGroup("Paths", 5, "jsonpath")]
    public string? AttributeTargetPath { get; set; }
}