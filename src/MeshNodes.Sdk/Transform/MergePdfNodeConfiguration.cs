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

    /// <summary>
    /// Optional path to write the merged PDF's byte length to (as a long), for
    /// feeding a following <c>CreateFileSystemUpdate@1</c>.
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }

    /// <summary>
    /// When <c>true</c>, the merged PDF is written to the per-execution scratch space
    /// and <see cref="TargetPathNodeConfiguration.TargetPath"/> receives a small
    /// <c>ScratchFileReference</c> (<c>scratchFileToken</c> + <c>length</c>) instead of a
    /// base64 string. This keeps large merged PDFs off the JSON data context / LOH — the
    /// downstream <c>CreateZipArchive@1</c> reads the entry by streaming from the scratch
    /// file. Falls back to base64 when no scratch space is available. Default <c>false</c>.
    /// </summary>
    [PropertyGroup("Behavior", 1)]
    public bool OutputAsScratchFile { get; set; } = false;
}
