using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration node object for downloading one file from an SFTP server. Read counterpart of
/// <c>SftpUpload@1</c>, which writes exactly one file.
/// </summary>
[NodeName("SftpDownload", 1)]
public record SftpDownloadNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Name of the global configuration for the SFTP server
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Static remote path of the file to read (set this or <see cref="RemotePathPath" />)
    /// </summary>
    [PropertyGroup("Data Mapping", 0)]
    public string? RemotePath { get; set; }

    /// <summary>
    /// Path in the data context to resolve the remote path dynamically; takes precedence over
    /// <see cref="RemotePath" />
    /// </summary>
    [PropertyGroup("Data Mapping", 1, "jsonpath")]
    public string? RemotePathPath { get; set; }

    /// <summary>
    /// Largest remote file this node will read, in bytes. The file is held in memory and then
    /// decoded to a string, so the remote side would otherwise decide how much memory the
    /// adapter allocates - a multi-gigabyte file takes the pod down and comes back on the next
    /// tick. A file past the limit fails this one node; the rest of the run continues.
    /// <para />
    /// The default of 100 MiB is far above any text export these nodes exist for and still
    /// leaves the peak - roughly three times the file, between the buffer, the byte array and
    /// the decoded string - inside what an adapter pod is normally given. Raise it only as far
    /// as the pod's memory limit allows. There is no unlimited setting.
    /// </summary>
    [PropertyGroup("Options", 2)]
    public long MaxFileSizeBytes { get; set; } = 100L * 1024 * 1024;

    /// <summary>
    /// Encoding the remote file is written in (e.g. utf-8, windows-1252, iso-8859-1). Unknown
    /// names are rejected when the pipeline configuration is bound, so a typo fails the
    /// deployment instead of the first download.
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
    /// How to handle bytes the configured encoding cannot represent: Replace substitutes the
    /// replacement character and logs a warning; Fail aborts the node, so no half-readable
    /// content travels downstream.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public EncodingErrorHandling OnEncodingError
    {
        get => _onEncodingError;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentException(
                    $"Unknown onEncodingError value '{(int)value}'. Use Replace or Fail.", nameof(value));
            }

            _onEncodingError = value;
        }
    }

    private EncodingErrorHandling _onEncodingError = EncodingErrorHandling.Replace;
}
