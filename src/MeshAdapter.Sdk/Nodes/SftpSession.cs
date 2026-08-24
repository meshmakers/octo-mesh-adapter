using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Raised when a remote file is larger than the caller allowed. Carries the numbers rather
/// than a finished sentence so the node can name the configuration property the operator has
/// to raise, which this layer does not know about.
/// </summary>
/// <param name="remotePath">Path of the file that was refused</param>
/// <param name="size">Size the server reported, or null when the file outgrew the cap mid-read</param>
/// <param name="maxBytes">Cap that was exceeded</param>
public sealed class SftpFileTooLargeException(string remotePath, long? size, long maxBytes)
    : Exception(size is null
        ? $"Remote file '{remotePath}' exceeds the {maxBytes} byte(s) allowed."
        : $"Remote file '{remotePath}' is {size} byte(s), which exceeds the {maxBytes} byte(s) allowed.")
{
    /// <summary>Path of the file that was refused.</summary>
    public string RemotePath { get; } = remotePath;

    /// <summary>Size the server reported, or null when the file outgrew the cap mid-read.</summary>
    public long? Size { get; } = size;

    /// <summary>Cap that was exceeded.</summary>
    public long MaxBytes { get; } = maxBytes;
}

/// <summary>
/// One entry of a remote directory listing.
/// </summary>
/// <param name="Name">File or directory name without its path</param>
/// <param name="FullPath">Full remote path of the entry</param>
/// <param name="IsDirectory">True when the entry is a directory</param>
/// <param name="Length">Size in bytes</param>
/// <param name="LastWriteTimeUtc">Time of the last write, in UTC</param>
public sealed record SftpEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long Length,
    DateTime LastWriteTimeUtc);

/// <summary>
/// An open SFTP session. Disposing it closes the connection and releases the server's
/// concurrency slot, so callers keep it in a using scope.
/// </summary>
public interface ISftpSession : IDisposable
{
    /// <summary>Lists a remote directory, files and directories alike.</summary>
    IReadOnlyList<SftpEntry> List(string remoteDirectory);

    /// <summary>
    /// Reads a remote file completely into memory. The file size decides how much memory the
    /// adapter allocates, so the caller states an upper bound: a file past it is refused with
    /// <see cref="SftpFileTooLargeException" /> instead of being read.
    /// </summary>
    byte[] Download(string remotePath, long maxBytes);

    /// <summary>Writes a stream to a remote path, overwriting an existing file.</summary>
    void Upload(Stream content, string remotePath);

    /// <summary>Creates the remote directory and any missing parent, if it does not exist.</summary>
    void EnsureDirectory(string remoteDirectory);
}

/// <summary>
/// Opens SFTP sessions, honouring the per-server concurrency limit and the optional host key
/// fingerprint. Single seam for every SFTP node, so connection behaviour cannot differ between
/// the read and the write direction.
/// </summary>
public interface ISftpSessionFactory
{
    /// <summary>
    /// Waits for a free slot of the named server configuration, connects and returns the open
    /// session. The slot counters live on the ETL context, the same scope
    /// <c>EMailSender@1</c> uses for its own concurrency limit.
    /// </summary>
    /// <param name="settings">Resolved connection settings</param>
    /// <param name="serverConfigurationName">Name the concurrency limit is tracked under</param>
    /// <param name="etlContext">The ETL context holding the slot counters</param>
    /// <param name="nodeContext">The node context, so a connection failure names the step it came from</param>
    /// <param name="cancellationToken">Cancels the wait for a free slot</param>
    /// <returns>The open session</returns>
    Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
        IMeshEtlContext etlContext, INodeContext nodeContext, CancellationToken cancellationToken = default);
}
