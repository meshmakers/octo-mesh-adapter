namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Sftp;

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

    /// <summary>Reads a remote file completely into memory.</summary>
    byte[] Download(string remotePath);

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
    /// session.
    /// </summary>
    /// <param name="settings">Resolved connection settings</param>
    /// <param name="serverConfigurationName">Name the concurrency limit is tracked under</param>
    /// <param name="cancellationToken">Cancels the wait for a free slot</param>
    /// <returns>The open session</returns>
    Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
        CancellationToken cancellationToken = default);
}
