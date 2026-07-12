using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for the node that reads the binary content of a
/// System.Reporting/FileSystemItem back into the pipeline (base64).
/// Read counterpart of <c>CreateFileSystemUpdate@1</c>.
/// </summary>
[NodeName("GetFileSystemContent", 1)]
public record GetFileSystemContentNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// JSON path to the RtId of the FileSystemItem whose content should be read
    /// </summary>
    [PropertyGroup("Entity", 0, "jsonpath")]
    public required string RtIdPath { get; set; }

    /// <summary>
    /// Optional output path for the stored file name
    /// </summary>
    [PropertyGroup("Output", 0, "jsonpath")]
    public string? FileNameTargetPath { get; set; }

    /// <summary>
    /// Optional output path for the stored content type
    /// </summary>
    [PropertyGroup("Output", 1, "jsonpath")]
    public string? ContentTypeTargetPath { get; set; }

    /// <summary>
    /// Optional output path for the stored content length in bytes
    /// </summary>
    [PropertyGroup("Output", 2, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }
}
