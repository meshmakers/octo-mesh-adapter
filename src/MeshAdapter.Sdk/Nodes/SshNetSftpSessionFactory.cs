using System.Collections.Concurrent;
using System.Text;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// SSH.NET implementation of <see cref="ISftpSessionFactory" />. One semaphore per server
/// configuration name bounds how many sessions are open against that server at a time. The
/// counters live on the ETL context rather than on this instance, which is the scope
/// <c>EMailSender@1</c> and the pre-seam upload node both use: a redeployed pipeline gets a
/// fresh registration and therefore picks up a changed limit.
/// </summary>
internal sealed class SshNetSftpSessionFactory : ISftpSessionFactory
{
    private const string SemaphoresKey = "SftpSessionFactory.Semaphores";
    private static readonly Lock SemaphoresLock = new();

    /// <inheritdoc />
    public async Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
        IMeshEtlContext etlContext, INodeContext nodeContext, CancellationToken cancellationToken = default)
    {
        if (settings.MaxConcurrentConnections <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidMaxConcurrentConnections(
                nodeContext, serverConfigurationName, settings.MaxConcurrentConnections);
        }

        var semaphore = GetOrCreateSemaphore(etlContext, serverConfigurationName, settings);

        // A wait that never ends turns one stalled transfer into a silent stop for every SFTP
        // node against that server. Zero keeps the previous unbounded behaviour.
        if (settings.WaitForSlotTimeoutSeconds > 0)
        {
            if (!await semaphore.WaitAsync(TimeSpan.FromSeconds(settings.WaitForSlotTimeoutSeconds),
                    cancellationToken))
            {
                throw MeshAdapterPipelineExecutionException.SftpSlotWaitTimedOut(
                    nodeContext, serverConfigurationName, settings.WaitForSlotTimeoutSeconds);
            }
        }
        else
        {
            await semaphore.WaitAsync(cancellationToken);
        }

        SftpClient? client = null;
        PrivateKeyFile? privateKeyFile = null;
        var hostKey = new HostKeyOutcome();
        try
        {
            client = CreateClient(settings, hostKey, out privateKeyFile);
            client.Connect();
            return new SshNetSftpSession(client, semaphore, privateKeyFile);
        }
        catch (Exception exception)
        {
            // The session never came into existence, so nothing will dispose it: close the
            // client and hand the slot back here, or the limit leaks one slot per failure.
            // The release sits in a finally because a slot lost here is lost for the lifetime
            // of the process, while a client that fails to dispose costs one socket.
            try
            {
                // Each disposal stands on its own: letting one throw would skip the next and
                // replace the failure the operator has to read - a host key mismatch would
                // surface as whatever the teardown complained about instead.
                DisposeQuietly(client);
                DisposeQuietly(privateKeyFile);
            }
            finally
            {
                semaphore.Release();
            }

            // SSH.NET refuses the connection itself once the handler reports CanTrust = false,
            // and surfaces it as a generic connection failure. Translating it here keeps the
            // library's own teardown - it sends SSH_MSG_DISCONNECT and unsubscribes its key
            // exchange handlers - while still telling the operator which key was presented.
            var translated = TranslateConnectFailure(exception, settings, hostKey, nodeContext);
            if (!ReferenceEquals(translated, exception))
            {
                throw translated;
            }

            throw;
        }
    }

    /// <summary>
    /// What the host key handler saw. The handler runs on SSH.NET's message listener thread
    /// while <c>Connect</c> blocks, so the outcome is carried out rather than thrown out:
    /// throwing from the handler skips the library's own refusal path and its teardown.
    /// </summary>
    internal sealed class HostKeyOutcome
    {
        public string? Presented { get; set; }

        public bool Refused { get; set; }
    }

    /// <summary>
    /// Decides whether a presented host key may be trusted and records what was seen. Reporting
    /// the verdict is what makes SSH.NET refuse the key exchange on its own terms; throwing from
    /// the handler would skip its refusal path and its teardown.
    /// </summary>
    internal static bool EvaluateHostKey(SftpServerSettings settings, string presentedFingerprint,
        HostKeyOutcome outcome)
    {
        outcome.Presented = presentedFingerprint;
        var trusted = SftpHostKeyVerifier.IsTrusted(settings.HostKeyFingerprint, presentedFingerprint);
        outcome.Refused = !trusted;
        return trusted;
    }

    /// <summary>
    /// Turns the generic connection failure that follows a refused host key into an error that
    /// names both fingerprints. Any other failure is returned untouched.
    /// </summary>
    internal static Exception TranslateConnectFailure(Exception exception, SftpServerSettings settings,
        HostKeyOutcome hostKey, INodeContext nodeContext)
    {
        if (exception is SshConnectionException && hostKey.Refused)
        {
            return MeshAdapterPipelineExecutionException.SftpHostKeyMismatch(
                nodeContext, settings.Host, settings.HostKeyFingerprint!, hostKey.Presented ?? "<unknown>");
        }

        return exception;
    }

    /// <summary>
    /// Reads a stream into memory and refuses more than <paramref name="maxBytes" />.
    /// <para />
    /// Copied by hand rather than through <c>DownloadFile</c> or <c>ReadAllBytes</c> so the cap
    /// holds while the bytes arrive: the size the server reported was true a moment ago, and a
    /// file that grows in between - or a server that lies about it - would otherwise decide how
    /// much memory this pod allocates. The reported size only sizes the buffer, which spares
    /// the repeated doubling a growing <see cref="MemoryStream" /> would do on the way to the
    /// same length; it is never trusted as a bound.
    /// </summary>
    /// <param name="source">The stream to read</param>
    /// <param name="remotePath">Path the stream belongs to, for the error message</param>
    /// <param name="maxBytes">Largest result that will be returned</param>
    /// <param name="expectedSize">Size the server reported, used only to size the buffer</param>
    /// <returns>The bytes that were read</returns>
    internal static byte[] ReadCapped(Stream source, string remotePath, long maxBytes, long expectedSize)
    {
        using var buffer = new MemoryStream(expectedSize > 0 && expectedSize <= int.MaxValue
            ? (int)expectedSize
            : 0);
        var chunk = new byte[81920];
        long total = 0;
        int read;

        while ((read = source.Read(chunk, 0, chunk.Length)) > 0)
        {
            total += read;
            if (total > maxBytes)
            {
                throw new SftpFileTooLargeException(remotePath, null, maxBytes);
            }

            buffer.Write(chunk, 0, read);
        }

        return buffer.ToArray();
    }

    /// <summary>
    /// Closes a session down and hands its slot back, without letting the teardown speak up.
    /// <para />
    /// This runs while a <c>using</c> block unwinds, which is usually carrying the failure the
    /// operator has to read: a server that refused the upload, a file past the size cap. A
    /// teardown that throws there replaces that failure with a connection complaint about the
    /// way down, and the steps behind it never run - the key material would stay in memory
    /// because the client tripped over the socket it no longer has. So each step stands on its
    /// own, and the slot goes back in a <c>finally</c>, since a slot lost here is lost for the
    /// lifetime of the process while an undisposed client costs one socket.
    /// </summary>
    /// <param name="disconnect">Closes the connection, if one is still standing</param>
    /// <param name="client">The client to release</param>
    /// <param name="privateKeyFile">Parsed key material to release, if the session holds any</param>
    /// <param name="semaphore">Counter the session took its slot from</param>
    internal static void DisposeSession(Action disconnect, IDisposable? client, IDisposable? privateKeyFile,
        SemaphoreSlim semaphore)
    {
        try
        {
            try
            {
                disconnect();
            }
            catch
            {
                // Nothing to report it to - the caller is unwinding already.
            }

            DisposeQuietly(client);
            DisposeQuietly(privateKeyFile);
        }
        finally
        {
            semaphore.Release();
        }
    }

    /// <summary>
    /// Disposes without letting a failing teardown replace the failure that caused it. Both
    /// callers run while something else is unwinding, so whatever is already on its way out is
    /// the exception worth reading.
    /// </summary>
    private static void DisposeQuietly(IDisposable? disposable)
    {
        try
        {
            disposable?.Dispose();
        }
        catch
        {
            // Nothing to report it to - the caller is already unwinding an error.
        }
    }

    private static SemaphoreSlim GetOrCreateSemaphore(IMeshEtlContext etlContext, string serverConfigurationName,
        SftpServerSettings settings)
    {
        lock (SemaphoresLock)
        {
            if (!etlContext.Properties.TryGetValue(SemaphoresKey, out var store) ||
                store is not ConcurrentDictionary<string, SemaphoreSlim> semaphores)
            {
                semaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
                etlContext.Properties[SemaphoresKey] = semaphores;
            }

            return semaphores.GetOrAdd(serverConfigurationName,
                _ => new SemaphoreSlim(settings.MaxConcurrentConnections, settings.MaxConcurrentConnections));
        }
    }

    private static SftpClient CreateClient(SftpServerSettings settings, HostKeyOutcome hostKey,
        out PrivateKeyFile? privateKeyFile)
    {
        SftpClient client;
        privateKeyFile = null;

        if (!string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            // The key file owns the parsed key material and is disposable, so it is handed to
            // the session and released with it. One session per file means an unreleased key
            // object and a plaintext copy of the key per file otherwise.
            using var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKey));
            privateKeyFile = string.IsNullOrWhiteSpace(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, settings.PrivateKeyPassphrase);

            client = new SftpClient(settings.Host, settings.Port, settings.Username, [privateKeyFile]);
        }
        else
        {
            client = new SftpClient(settings.Host, settings.Port, settings.Username,
                settings.Password ?? string.Empty);
        }

        // Past this point the client exists but the caller does not hold it yet, so a failure
        // here would leak it: its socket and its key exchange state would stay alive until the
        // finalizer ran, once per failed attempt.
        try
        {
            if (settings.ConnectTimeoutSeconds > 0)
            {
                client.ConnectionInfo.Timeout = TimeSpan.FromSeconds(settings.ConnectTimeoutSeconds);
            }

            if (settings.OperationTimeoutSeconds > 0)
            {
                client.OperationTimeout = TimeSpan.FromSeconds(settings.OperationTimeoutSeconds);
            }

            client.HostKeyReceived += (_, e) => e.CanTrust = EvaluateHostKey(settings, e.FingerPrintSHA256, hostKey);
        }
        catch
        {
            DisposeQuietly(client);
            throw;
        }

        return client;
    }

    private sealed class SshNetSftpSession(SftpClient client, SemaphoreSlim semaphore,
        PrivateKeyFile? privateKeyFile) : ISftpSession
    {
        public IReadOnlyList<SftpEntry> List(string remoteDirectory)
        {
            return client.ListDirectory(remoteDirectory)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new SftpEntry(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTimeUtc))
                .ToList();
        }

        public byte[] Download(string remotePath, long maxBytes)
        {
            // Ask before reading. A file past the cap is then refused without a byte crossing
            // the wire, and the message can name the size the server reported.
            var size = client.GetAttributes(remotePath).Size;
            if (size > maxBytes)
            {
                throw new SftpFileTooLargeException(remotePath, size, maxBytes);
            }

            using var remote = client.OpenRead(remotePath);
            return ReadCapped(remote, remotePath, maxBytes, size);
        }

        public void Upload(Stream content, string remotePath)
        {
            client.UploadFile(content, remotePath, true);
        }

        public void EnsureDirectory(string remoteDirectory)
        {
            var isAbsolute = remoteDirectory.StartsWith('/');
            var parts = remoteDirectory.Split('/', StringSplitOptions.RemoveEmptyEntries);
            var currentPath = isAbsolute ? "" : ".";

            foreach (var part in parts)
            {
                currentPath += "/" + part;
                try
                {
                    client.GetAttributes(currentPath);
                }
                catch (SftpPathNotFoundException)
                {
                    client.CreateDirectory(currentPath);
                }
            }
        }

        private bool _disposed;

        public void Dispose()
        {
            // Releasing twice would hand out a slot that was never taken and raise the limit
            // for good. Every caller in this assembly disposes once, but the interface is
            // public and the failure would be silent.
            if (_disposed)
            {
                return;
            }

            _disposed = true;

            // Reading IsConnected is part of the step: it asks the session, so a connection
            // that fell over underneath us can fail here just as the disconnect can.
            DisposeSession(() =>
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }
            }, client, privateKeyFile, semaphore);
        }
    }
}
