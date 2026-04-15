using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromPipelineTriggerEvent
/// </summary>
[NodeName("FromWatchRtEntity", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record FromWatchRtEntityNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// Gets or sets the update types.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public required TriggerUpdateTypes UpdateTypes { get; set; }

    /// <summary>
    /// The type identifier of the object to be watched.
    /// </summary>
    [PropertyGroup("Entity", 0, "ckTypeSelector")]
    public required RtCkId<CkTypeId> CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the runtime identifier of an object to filter by (optional).
    /// </summary>
    [PropertyGroup("Entity", 1)]
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// Gets or sets optional field filters to filter by on the version before storing runtime entity object.
    /// </summary>
    [PropertyGroup("Query", 0)]
    public ICollection<FieldFilterDto>? BeforeFieldFilters { get; set; }

    /// <summary>
    /// Gets or sets optional field filters to filter by on the current runtime entity object.
    /// </summary>
    [PropertyGroup("Query", 1)]
    public ICollection<FieldFilterDto>? FieldFilters { get; set; }
}
