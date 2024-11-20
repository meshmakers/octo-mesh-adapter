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
    public required TriggerUpdateTypes UpdateTypes { get; set; }
    
    /// <summary>
    /// The type identifier of the object to be watched.
    /// </summary>
    public required CkId<CkTypeId> CkTypeId { get; set; }

    /// <summary>
    /// Gets or sets the runtime identifier of an object to filter by (optional).
    /// </summary>
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// Gets or sets optional field filters to filter by on the version before storing runtime entity object.
    /// </summary>
    public ICollection<FieldFilterDto>? BeforeFieldFilters { get; set; }
    
    /// <summary>
    /// Gets or sets optional field filters to filter by on the current runtime entity object.
    /// </summary>
    public ICollection<FieldFilterDto>? FieldFilters { get; set; }
}
