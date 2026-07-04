using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for the ImportDataPointMappings node.
///
/// The node reads a mapping export document (produced by
/// <see cref="ExportDataPointMappingsNodeConfiguration"/>) from the data
/// context and resolves each entry's source/target back to runtime entities.
/// Resolution order per endpoint: RtId (valid only for same-tenant round
/// trips) → identity attribute value (e.g. LoxoneUuid — survives tenant
/// re-initialisation because the source system assigns it) → unique entity
/// name.
///
/// Resolved entries are emitted in the GenerateDataPointMappings suggestion
/// shape (plus an <c>enabled</c> field), so the SAME downstream persistence
/// chain (GetOrCreate by name + CreateUpdateInfo + CreateAssociationUpdate +
/// ApplyChanges) consumes generated, AI-suggested and imported mappings alike.
/// Entries that cannot be resolved are reported in the statistics object for
/// manual follow-up — they are never guessed.
/// </summary>
[NodeName("ImportDataPointMappings", 1)]
public record ImportDataPointMappingsNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// JSONPath of the mapping export document in the data context (e.g.
    /// "$.importDocument" after a FromHttpRequest upload).
    /// </summary>
    [PropertyGroup("Source", 0, "jsonpath")]
    public required string Path { get; set; }

    /// <summary>
    /// Optional JSONPath to write the import statistics to (total, resolved,
    /// unresolved entries with the reason per entry).
    /// </summary>
    [PropertyGroup("Output", 0, "jsonpath")]
    public string? StatisticsTargetPath { get; set; }
}
