using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// SSH.NET implementation of <see cref="ISftpSessionFactory" />. One semaphore per server
/// configuration name bounds how many sessions this process opens against that server at the
/// same time.
/// </summary>
public sealed class SshNetSftpSessionFactory : ISftpSessionFactory
{
    private readonly ConcurrentDictionary<string, SemaphoreSlim> _semaphores = new();

    /// <inheritdoc />
    public async Task<ISftpSession> ConnectAsync(SftpServerSettings settings, string serverConfigurationName,
        CancellationToken cancellationToken = default)
    {
        if (settings.MaxConcurrentConnections <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidMaxConcurrentConnections(
                serverConfigurationName, settings.MaxConcurrentConnections);
        }

        var semaphore = _semaphores.GetOrAdd(serverConfigurationName,
            _ => new SemaphoreSlim(settings.MaxConcurrentConnections, settings.MaxConcurrentConnections));

        await semaphore.WaitAsync(cancellationToken);

        SftpClient? client = null;
        var hostKey = new HostKeyOutcome();
        try
        {
            client = CreateClient(settings, hostKey);
            client.Connect();
            return new SshNetSftpSession(client, semaphore);
        }
        catch (Exception exception)
        {
            // The session never came into existence, so nothing will dispose it: close the
            // client and hand the slot back here, or the limit leaks one slot per failure.
            // The release sits in a finally because a slot lost here is lost for the lifetime
            // of the process, while a client that fails to dispose costs one socket.
            try
            {
                client?.Dispose();
            }
            finally
            {
                semaphore.Release();
            }

            // SSH.NET refuses the connection itself once the handler reports CanTrust = false,
            // and surfaces it as a generic connection failure. Translating it here keeps the
            // library's own teardown - it sends SSH_MSG_DISCONNECT and unsubscribes its key
            // exchange handlers - while still telling the operator which key was presented.
            if (exception is SshConnectionException && hostKey.Refused)
            {
                throw MeshAdapterPipelineExecutionException.SftpHostKeyMismatch(
                    settings.Host, settings.HostKeyFingerprint!, hostKey.Presented ?? "<unknown>");
            }

            throw;
        }
    }

    /// <summary>
    /// What the host key handler saw. The handler runs on SSH.NET's message listener thread
    /// while <c>Connect</c> blocks, so the outcome is carried out rather than thrown out:
    /// throwing from the handler skips the library's own refusal path and its teardown.
    /// </summary>
    private sealed class HostKeyOutcome
    {
        public string? Presented { get; set; }

        public bool Refused { get; set; }
    }

    private static SftpClient CreateClient(SftpServerSettings settings, HostKeyOutcome hostKey)
    {
        SftpClient client;

        if (!string.IsNullOrWhiteSpace(settings.PrivateKey))
        {
            var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(settings.PrivateKey));
            var privateKeyFile = string.IsNullOrWhiteSpace(settings.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, settings.PrivateKeyPassphrase);

            client = new SftpClient(settings.Host, settings.Port, settings.Username, [privateKeyFile]);
        }
        else
        {
            client = new SftpClient(settings.Host, settings.Port, settings.Username,
                settings.Password ?? string.Empty);
        }

        client.HostKeyReceived += (_, e) =>
        {
            // Report the verdict rather than throwing: SSH.NET reads CanTrust back from the
            // handler and refuses the key exchange itself, which is the path its teardown is
            // written for. The presented fingerprint is carried out so the caller can name it.
            hostKey.Presented = e.FingerPrintSHA256;
            e.CanTrust = SftpHostKeyVerifier.IsTrusted(settings.HostKeyFingerprint, e.FingerPrintSHA256);
            hostKey.Refused = !e.CanTrust;
        };

        return client;
    }

    private sealed class SshNetSftpSession(SftpClient client, SemaphoreSlim semaphore) : ISftpSession
    {
        public IReadOnlyList<SftpEntry> List(string remoteDirectory)
        {
            return client.ListDirectory(remoteDirectory)
                .Where(f => f.Name != "." && f.Name != "..")
                .Select(f => new SftpEntry(f.Name, f.FullName, f.IsDirectory, f.Length, f.LastWriteTimeUtc))
                .ToList();
        }

        public byte[] Download(string remotePath)
        {
            using var stream = new MemoryStream();
            client.DownloadFile(remotePath, stream);
            return stream.ToArray();
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

        public void Dispose()
        {
            try
            {
                // Disconnect and Dispose are nested so a failing disconnect - a connection
                // dropped underneath us, for instance - still disposes the client. A plain
                // using block gave that guarantee before this seam existed.
                try
                {
                    if (client.IsConnected)
                    {
                        client.Disconnect();
                    }
                }
                finally
                {
                    client.Dispose();
                }
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
