using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the MapToRecordArray node.
/// Transforms a JSON object (key/value map) into an array of CK records suitable for
/// storage in a RecordArray attribute. Each map entry becomes one record with two
/// attributes: the key and the value.
/// </summary>
[NodeName("MapToRecordArray", 1)]
public record MapToRecordArrayNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Semantic-versioned full name of the CK record type to produce
    /// (e.g. "Loxone/LoxoneState").
    /// </summary>
    [PropertyGroup("Record", 0)]
    public required string CkRecordId { get; set; }

    /// <summary>
    /// Name of the record attribute that receives the map key (e.g. "StateName").
    /// </summary>
    [PropertyGroup("Record", 1)]
    public required string KeyAttributeName { get; set; }

    /// <summary>
    /// Name of the record attribute that receives the map value (e.g. "StateUuid").
    /// </summary>
    [PropertyGroup("Record", 2)]
    public required string ValueAttributeName { get; set; }
}
