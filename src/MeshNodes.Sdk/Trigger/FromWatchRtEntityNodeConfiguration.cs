using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for node FromPipelineTriggerEvent
/// </summary>
[NodeName("FromWatchRtEntity", 1)]
[NodeRequiresRunningProcess]
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
    /// Opt-in for delete events (AB#9770). <c>false</c> (default): no delete event reaches the pipeline, whatever
    /// <see cref="UpdateTypes" /> says - the behaviour of every watch before this setting existed. <c>true</c> together
    /// with <c>Delete</c> in <see cref="UpdateTypes" />: a delete of the watched type runs the pipeline with
    /// <c>$.UpdateType</c> <c>Delete</c>, <c>$.Document</c> null and the deleted entity's id at <c>$.DocumentRtId</c>
    /// (also set for every other event); <c>$.DocumentBeforeChange</c> holds the deleted entity when the type's
    /// collection captures pre-images. Without pre-images the watch must name the collection root of the type and no
    /// field filter (the deleted document is not available to match); any other combination fails the trigger start.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public bool IncludeDeletes { get; set; }

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
