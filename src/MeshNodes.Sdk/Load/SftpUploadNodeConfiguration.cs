using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for uploading a file via SFTP
/// </summary>
[NodeName("SftpUpload", 1)]
public record SftpUploadNodeConfiguration : PathNodeConfiguration
{
    /// <summary>
    /// Name of the global configuration for the SFTP server
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Remote directory path on the SFTP server
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string RemoteDirectory { get; set; }

    /// <summary>
    /// Static file name for the uploaded file
    /// </summary>
    [PropertyGroup("Data Mapping", 0)]
    public string? FileName { get; set; }

    /// <summary>
    /// Path in the data context to resolve the file name dynamically
    /// </summary>
    [PropertyGroup("Data Mapping", 1, "jsonpath")]
    public string? FileNamePath { get; set; }

    /// <summary>
    /// Static RtId of a binary file in MongoDB large binary storage
    /// </summary>
    [PropertyGroup("Data Mapping", 2)]
    public string? FileRtId { get; set; }

    /// <summary>
    /// Path in the data context to resolve the RtId of a binary file dynamically
    /// </summary>
    [PropertyGroup("Data Mapping", 3, "jsonpath")]
    public string? FileRtIdPath { get; set; }
}
