using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// One file on the <see cref="InMemorySftpServer" />.
/// </summary>
internal sealed record RemoteFile(long Length, DateTime LastWriteTimeUtc, string Content = "",
    bool IsDirectory = false);

/// <summary>
/// An SFTP server in memory for the node tests: files by full path, a log of every operation in
/// the order it reached the server, and hooks that run before and after each operation, so a test
/// can change the server between two steps of a node - a file delivered again, a refused request,
/// a connection that breaks, an answer that is lost. A test then asserts the state the server is
/// left in, not only which calls were made.
/// <para />
/// A move onto an existing name follows the SFTP version 3 rule OpenSSH implements for the plain
/// rename: an error, and nothing moves. The node only ever moves to a fresh name, so a change that
/// moved onto a name in use would fail here rather than pass. What the double does not show: the
/// path resolution of a real server (symbolic links) - the integration tests against a real server
/// cover that.
/// </summary>
internal sealed class InMemorySftpServer
{
    public Dictionary<string, RemoteFile> Files { get; } = new(StringComparer.Ordinal);

    public List<string> Operations { get; } = [];

    /// <summary>
    /// Every operation after this is set fails with a connection error. Set it from
    /// <see cref="Before" /> to break the connection at a given step.
    /// </summary>
    public bool Disconnected { get; set; }

    /// <summary>
    /// Runs before every operation with the operation's name and the path it addresses (the
    /// source path for a move). Throwing here fails the operation before it changed anything.
    /// </summary>
    public Action<string, string>? Before { get; set; }

    /// <summary>
    /// Runs after an operation was carried out, with the same arguments as <see cref="Before" />.
    /// Throwing here stands in for a lost answer: the server did it, the caller sees a failure.
    /// </summary>
    public Action<string, string>? After { get; set; }

    /// <summary>The new name of the last move that was asked for, set before the move runs or fails.</summary>
    public string? LastMoveTarget { get; private set; }

    public ISftpSession OpenSession()
    {
        return new Session(this);
    }

    private void Enter(string operation, string path, string logEntry)
    {
        Before?.Invoke(operation, path);
        if (Disconnected)
        {
            throw new InvalidOperationException("Connection lost.");
        }

        Operations.Add(logEntry);
    }

    private sealed class Session(InMemorySftpServer server) : ISftpSession
    {
        public SftpEntry? GetEntry(string remotePath)
        {
            server.Enter(nameof(GetEntry), remotePath, $"GetEntry {remotePath}");
            return server.Files.TryGetValue(remotePath, out var file)
                ? new SftpEntry(remotePath[(remotePath.LastIndexOf('/') + 1)..], remotePath, file.IsDirectory,
                    file.Length, file.LastWriteTimeUtc)
                : null;
        }

        public bool MoveToNewName(string remotePath, string newRemotePath)
        {
            server.LastMoveTarget = newRemotePath;
            server.Enter(nameof(MoveToNewName), remotePath, $"Move {remotePath} -> {newRemotePath}");
            if (!server.Files.Remove(remotePath, out var file))
            {
                return false;
            }

            if (server.Files.ContainsKey(newRemotePath))
            {
                server.Files[remotePath] = file;
                // What OpenSSH answers when link() meets an existing name: EEXIST maps to the
                // generic SSH2_FX_FAILURE.
                throw new InvalidOperationException("Failure");
            }

            server.Files[newRemotePath] = file;
            server.After?.Invoke(nameof(MoveToNewName), remotePath);
            return true;
        }

        public bool Delete(string remotePath)
        {
            server.Enter(nameof(Delete), remotePath, $"Delete {remotePath}");
            if (!server.Files.TryGetValue(remotePath, out var file))
            {
                return false;
            }

            if (file.IsDirectory)
            {
                throw new InvalidOperationException("Failure");
            }

            server.Files.Remove(remotePath);
            server.After?.Invoke(nameof(Delete), remotePath);
            return true;
        }

        // The node under test has no business listing, reading or writing: a call here fails the
        // test instead of quietly answering.
        public IReadOnlyList<SftpEntry> List(string remoteDirectory) =>
            throw new NotSupportedException($"List {remoteDirectory}");

        public byte[] Download(string remotePath, long maxBytes) =>
            throw new NotSupportedException($"Download {remotePath}");

        public void Upload(Stream content, string remotePath) =>
            throw new NotSupportedException($"Upload {remotePath}");

        public void EnsureDirectory(string remoteDirectory) =>
            throw new NotSupportedException($"EnsureDirectory {remoteDirectory}");

        public void Dispose()
        {
            server.Operations.Add("Dispose");
        }
    }
}
