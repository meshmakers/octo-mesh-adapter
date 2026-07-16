using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for <c>MergePdf@1</c>. Concatenates several PDFs (given as an
/// array of base64 strings at <see cref="SourceTargetPathNodeConfiguration.Path"/>,
/// in order) into one PDF written as base64 to
/// <see cref="TargetPathNodeConfiguration.TargetPath"/>. Used to prepend a
/// generated cover sheet to an original document.
/// </summary>
[NodeName("MergePdf", 1)]
public record MergePdfNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// When <c>true</c>, a PDF that cannot be imported (encrypted, corrupt or an
    /// unsupported version) aborts the node. When <c>false</c> (default) the
    /// offending entry is skipped with a warning and the remaining PDFs are
    /// merged — so a broken original never silently loses the whole package.
    /// </summary>
    [PropertyGroup("Behavior", 0)]
    public bool FailOnInvalidPdf { get; set; } = false;
}
