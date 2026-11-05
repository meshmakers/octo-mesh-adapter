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
    /// Length in bytes of the file as it was listed - the <c>length</c> of an <c>SftpList@1</c>
    /// element (set this or <c>ExpectedLengthPath</c>). Together with the last write time it is
    /// the identity of the listed file: once either is configured, the node deletes the file only
    /// while it still has that identity, and both have to be configured.
    /// </summary>
    [PropertyGroup("Data Mapping", 2)]
    public long? ExpectedLength { get; set; }

    /// <summary>
    /// Path in the data context to the expected length; takes precedence over
    /// <c>ExpectedLength</c>. A JSON integer or a string of digits.
    /// </summary>
    [PropertyGroup("Data Mapping", 3, "jsonpath")]
    public string? ExpectedLengthPath { get; set; }

    /// <summary>
    /// Last write time of the file as it was listed - the <c>lastWriteTimeUtc</c> of an
    /// <c>SftpList@1</c> element, in the form that node writes: <c>yyyy-MM-ddTHH:mm:ss</c>, seven
    /// fractional digits and <c>Z</c>; zero to seven fractional digits are accepted (set this or
    /// <c>ExpectedLastWriteTimeUtcPath</c>). <c>0001-01-01T00:00:00Z</c>, which that node writes
    /// for a server that reports no last write time, is refused.
    /// </summary>
    [PropertyGroup("Data Mapping", 4)]
    public string? ExpectedLastWriteTimeUtc { get; set; }

    /// <summary>
    /// Path in the data context to the expected last write time; takes precedence over
    /// <c>ExpectedLastWriteTimeUtc</c>.
    /// </summary>
    [PropertyGroup("Data Mapping", 5, "jsonpath")]
    public string? ExpectedLastWriteTimeUtcPath { get; set; }

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

    /// <summary>
    /// What to do when the file no longer has the expected identity: it stays on the server
    /// under its name either way. <c>Fail</c> reports it as a node failure, <c>Warn</c> logs a
    /// warning and lets the chain continue. <c>Warn</c> without an expected identity is refused
    /// before connecting, because nothing would be compared; <c>Fail</c> cannot be told from the
    /// default and has no effect without one.
    /// <para />
    /// The strict reading is the default and the zero member. There is deliberately no member
    /// that deletes a file whose identity differs. A file the node has to leave under its
    /// temporary name fails the node whatever this says: <c>SftpList@1</c> skips that name unless
    /// its pattern asks for it, so the file needs a person.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public IdentityMismatchHandling OnIdentityMismatch
    {
        get => _onIdentityMismatch;
        set
        {
            if (!Enum.IsDefined(value))
            {
                throw new ArgumentException(
                    $"Unknown onIdentityMismatch value '{(int)value}'. Use Fail or Warn.", nameof(value));
            }

            _onIdentityMismatch = value;
        }
    }

    private IdentityMismatchHandling _onIdentityMismatch = IdentityMismatchHandling.Fail;
}

/// <summary>
/// How <c>SftpDelete@1</c> reacts to a file that no longer has the expected identity. The file
/// stays on the server in both cases.
/// </summary>
public enum IdentityMismatchHandling
{
    /// <summary>Report the changed file as a node failure (default)</summary>
    Fail = 0,

    /// <summary>Log a warning and carry on</summary>
    Warn = 1
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
