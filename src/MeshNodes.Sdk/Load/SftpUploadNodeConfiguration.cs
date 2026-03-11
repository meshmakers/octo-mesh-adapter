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
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Remote directory path on the SFTP server
    /// </summary>
    public required string RemoteDirectory { get; set; }

    /// <summary>
    /// Static file name for the uploaded file
    /// </summary>
    public string? FileName { get; set; }

    /// <summary>
    /// Path in the data context to resolve the file name dynamically
    /// </summary>
    public string? FileNamePath { get; set; }

    /// <summary>
    /// Static RtId of a binary file in MongoDB large binary storage
    /// </summary>
    public string? FileRtId { get; set; }

    /// <summary>
    /// Path in the data context to resolve the RtId of a binary file dynamically
    /// </summary>
    public string? FileRtIdPath { get; set; }
}
