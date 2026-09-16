using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Load;

/// <summary>
/// Configuration node object for deleting one file from an SFTP server: it removes the file
/// <c>SftpDownload@1</c> read, once the content has been processed, which is the contract a
/// file drop-off works by.
/// </summary>
[NodeName("SftpDelete", 1)]
public record SftpDeleteNodeConfiguration : NodeConfiguration
{
    /// <summary>
    /// Name of the global configuration for the SFTP server
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ServerConfiguration { get; set; }

    /// <summary>
    /// Static remote path of the file to delete (set this or <c>RemotePathPath</c>)
    /// </summary>
    [PropertyGroup("Data Mapping", 0)]
    public string? RemotePath { get; set; }

    /// <summary>
    /// Path in the data context to resolve the remote path dynamically; takes precedence over
    /// <c>RemotePath</c>
    /// </summary>
    [PropertyGroup("Data Mapping", 1, "jsonpath")]
    public string? RemotePathPath { get; set; }

    /// <summary>
    /// What to do when the file is not there any more: <c>Fail</c> reports it as a node
    /// failure, <c>Ignore</c> logs a warning and lets the chain continue, because the goal
    /// state - the file is gone - is reached either way.
    /// <para />
    /// The default is the strict one, and it is the zero member as well, so a definition that
    /// says nothing about this cannot end up lenient. A pipeline that repeats a partially
    /// finished run wants <c>Ignore</c>, but that is something the pipeline states rather than
    /// something this node grants every caller.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public MissingFileHandling OnMissingFile
    {
        get => _onMissingFile;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentException(
                    $"Unknown onMissingFile value '{(int)value}'. Use Fail or Ignore.", nameof(value));
            }

            _onMissingFile = value;
        }
    }

    private MissingFileHandling _onMissingFile = MissingFileHandling.Fail;
}

/// <summary>
/// How <c>SftpDelete@1</c> reacts to a file that is not there any more.
/// </summary>
public enum MissingFileHandling
{
    /// <summary>Report the missing file as a node failure (default)</summary>
    Fail = 0,

    /// <summary>Log a warning and carry on</summary>
    Ignore = 1
}
