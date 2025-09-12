using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration node object for update a file system object
/// </summary>
[NodeName("CreateFileSystemUpdate", 1)]
public record CreateFileSystemUpdateNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// The path to the RtEntityId
    /// </summary>
    public string? RtIdPath { get; set; }

    /// <summary>
    /// The runtime id of the object
    /// </summary>
    public OctoObjectId? RtId { get; set; }

    /// <summary>
    /// When true, the RtId will be generated if it is not existing when RtIdPath is set
    /// </summary>
    public bool GenerateRtId { get; set; } = false;

    /// <summary>
    /// Gets or sets the file name
    /// </summary>
    public string? FileName { get; set; } = null!;

    /// <summary>
    /// Gets or sets the path to the file name
    /// </summary>
    public string? FileNamePath { get; set; } = null!;

    /// <summary>
    /// When true, the file name will be generated based on the content type if <see cref="FileName"/> and <see cref="FileNamePath"/> are not set
    /// </summary>
    public bool GenerateFileName { get; set; } = false;

    /// <summary>
    /// Gets or sets the content type of the file
    /// </summary>
    public string? ContentType { get; set; } = null!;

    /// <summary>
    /// Gets or sets the path to the content type of the file
    /// </summary>
    public string? ContentTypePath { get; set; } = null!;

    /// <summary>
    /// Gets or sets the content length of the file
    /// </summary>
    public long? ContentLength { get; set; } = null!;

    /// <summary>
    /// Gets or sets the path to the content length of the file
    /// </summary>
    public string? ContentLengthPath { get; set; } = null!;

    /// <summary>
    /// The path to the RtWellKnownName if available
    /// </summary>
    public string? RtWellKnownNamePath { get; set; }

    /// <summary>
    /// Gets or sets the RtWellKnownName of the file system root folder
    /// </summary>
    public required string RootFolderWellKnownName { get; set; }
}