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
    /// content length explicitly. Ignored in <see cref="PersistAsFileSystemItem"/>
    /// mode (the node persists the archive itself).
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }

    /// <summary>
    /// When <c>true</c>, the archive is streamed to the per-execution scratch space and
    /// persisted directly as a <c>System.Reporting/FileSystemItem</c> — the node writes
    /// the resulting item's RtId (as a string) to
    /// <see cref="TargetPathNodeConfiguration.TargetPath"/> instead of a base64 archive.
    /// This avoids the three large-object-heap copies of the whole ZIP (MemoryStream +
    /// ToArray + base64) and a separate <c>CreateFileSystemUpdate@1</c> round-trip that
    /// otherwise OOM a big fiscal-year handover export (AB#4642). Requires
    /// <see cref="RootFolderWellKnownName"/>. Entries may carry their content as
    /// <c>contentBase64</c> or as a scratch reference (<c>scratchFileToken</c>).
    /// </summary>
    [PropertyGroup("FileSystemItem", 0)]
    public bool PersistAsFileSystemItem { get; set; } = false;

    /// <summary>RtWellKnownName of the file-system root folder to place the item under (persist mode).</summary>
    [PropertyGroup("FileSystemItem", 1)]
    public string? RootFolderWellKnownName { get; set; }

    /// <summary>Static file name for the persisted archive (persist mode).</summary>
    [PropertyGroup("FileSystemItem", 2)]
    public string? FileName { get; set; }

    /// <summary>Path to the file name for the persisted archive (persist mode).</summary>
    [PropertyGroup("FileSystemItem", 3, "jsonpath")]
    public string? FileNamePath { get; set; }

    /// <summary>Content type of the persisted archive (persist mode). Defaults to <c>application/zip</c>.</summary>
    [PropertyGroup("FileSystemItem", 4)]
    public string ContentType { get; set; } = "application/zip";

    /// <summary>When true, generates the item's RtId (persist mode).</summary>
    [PropertyGroup("FileSystemItem", 5)]
    public bool GenerateRtId { get; set; } = true;
}
