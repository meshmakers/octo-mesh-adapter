using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for <c>CreateZipArchive@1</c>. Bundles a set of files into a
/// single ZIP archive written as base64 to
/// <see cref="TargetPathNodeConfiguration.TargetPath"/>.
/// <para>
/// The value at <see cref="SourceTargetPathNodeConfiguration.Path"/> must be a
/// JSON array of entries of the shape
/// <code>{ "fileName": "AP/RE-2025-001.pdf", "contentBase64": "JVBERi0..." }</code>.
/// A <c>fileName</c> may contain forward slashes to create folders inside the
/// archive (e.g. group by AP/AR). Keys are matched case-insensitively.
/// </para>
/// </summary>
[NodeName("CreateZipArchive", 1)]
public record CreateZipArchiveNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Optional path to write the archive's byte length to (as a long). Handy for
    /// feeding a following <c>CreateFileSystemUpdate@1</c>, which requires the
    /// content length explicitly.
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }
}
