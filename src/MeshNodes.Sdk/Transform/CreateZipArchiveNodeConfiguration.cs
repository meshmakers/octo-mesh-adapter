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
/// <para>
/// Instead of a pre-composed <c>fileName</c>, an entry may carry
/// <c>pathSegments</c> — an array of folder names ending with the file name
/// (incl. extension), e.g. <c>["FY 2026", "2026-01", "2026-01-15_ACME.pdf"]</c>.
/// Each segment is sanitized for file-system use (path separators and other
/// invalid characters are replaced), so data-derived values such as vendor
/// names cannot create unintended folders or invalid entry names.
/// </para>
/// </summary>
[NodeName("CreateZipArchive", 1)]
public record CreateZipArchiveNodeConfiguration : SourceTargetPathNodeConfiguration
{

    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). A missing value resolves to <c>Caller</c> so existing
    /// pipelines are unchanged (AB#5127).
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;
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

    /// <summary>
    /// When <c>true</c>, a running number (<c>_001</c>, <c>_002</c>, …) is inserted
    /// before the extension of every content entry's file name, guaranteeing unique
    /// entry names even when several entries would otherwise resolve to the same name
    /// (e.g. two invoices of the same vendor on the same day).
    /// </summary>
    [PropertyGroup("Naming", 0)]
    public bool AppendSequenceNumber { get; set; } = false;

    /// <summary>
    /// When set (e.g. <c>belege.csv</c>), a CSV manifest with this name is written as
    /// the first archive entry. Each content entry may carry a <c>manifest</c> object
    /// whose fields become the CSV columns (ordered union across entries); the entry's
    /// final archive path (after sanitizing and sequence numbering) is prepended as the
    /// <see cref="ManifestFileNameColumn"/> column. Semicolon-separated, UTF-8 with BOM.
    /// </summary>
    [PropertyGroup("Manifest", 0)]
    public string? ManifestFileName { get; set; }

    /// <summary>Column name for the entry path column of the manifest. Defaults to <c>FileName</c>.</summary>
    [PropertyGroup("Manifest", 1)]
    public string ManifestFileNameColumn { get; set; } = "FileName";

    /// <summary>Value delimiter of the manifest CSV. Defaults to <c>;</c> (BMD/Excel-friendly).</summary>
    [PropertyGroup("Manifest", 2)]
    public string ManifestDelimiter { get; set; } = ";";
}
