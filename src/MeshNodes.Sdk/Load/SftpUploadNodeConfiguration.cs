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

    /// <summary>
    /// Encoding used for string content (e.g. utf-8, windows-1252, iso-8859-1).
    /// Applies only to content resolved via <see cref="PathNodeConfiguration.Path"/>;
    /// binary sources are uploaded byte-for-byte. Unknown names are rejected when the
    /// pipeline configuration is bound, so a typo fails the deployment instead of the
    /// first upload.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public string Encoding
    {
        get => _encoding;
        set
        {
            SftpUploadEncoding.Resolve(value);
            _encoding = value;
        }
    }

    private string _encoding = "utf-8";

    /// <summary>
    /// How to handle characters the configured encoding cannot represent:
    /// Replace substitutes a single '?' per character and logs a warning naming the
    /// affected code points; Fail aborts before the upload starts, so no degraded file
    /// reaches the target.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public EncodingErrorHandling OnEncodingError { get; set; } = EncodingErrorHandling.Replace;
}

/// <summary>
/// Handling of characters that cannot be represented in the configured upload encoding
/// </summary>
public enum EncodingErrorHandling
{
    /// <summary>Replace each unencodable character with '?' and log a warning</summary>
    Replace,

    /// <summary>Abort the upload before any data is written to the target</summary>
    Fail
}
