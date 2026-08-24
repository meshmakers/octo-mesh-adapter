using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration node object for listing files on an SFTP server. Emits metadata only; the
/// content of a listed file is read with <c>SftpDownload@1</c>.
/// </summary>
[NodeName("SftpList", 1)]
public record SftpListNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the global configuration for the SFTP server
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Remote directory to list
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string RemoteDirectory { get; set; }

    /// <summary>
    /// Glob the file name must match: '*' any run of characters, '?' exactly one, anchored at
    /// both ends, case insensitive, every other character literal
    /// </summary>
    [PropertyGroup("Filter", 0)]
    public required string FilePattern { get; set; }

    /// <summary>
    /// Omit entries whose last write is younger than this, so a file still being written is
    /// picked up on a later run instead of being read half finished
    /// </summary>
    [PropertyGroup("Filter", 1)]
    public int MinFileAgeSeconds { get; set; }
}
