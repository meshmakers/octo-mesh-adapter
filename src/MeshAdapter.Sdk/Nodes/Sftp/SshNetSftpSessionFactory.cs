using System.Collections.Concurrent;
using System.Text;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Sftp;

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
        try
        {
            client = CreateClient(settings);
            client.Connect();
            return new SshNetSftpSession(client, semaphore);
        }
        catch
        {
            // The session never came into existence, so nothing will dispose it: close the
            // client and hand the slot back here, or the limit leaks one slot per failure.
            client?.Dispose();
            semaphore.Release();
            throw;
        }
    }

    private static SftpClient CreateClient(SftpServerSettings settings)
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
            if (SftpHostKeyVerifier.IsTrusted(settings.HostKeyFingerprint, e.FingerPrintSHA256))
            {
                return;
            }

            // Refusing here aborts Connect(). The message names both fingerprints so an
            // operator can tell a deliberately rotated key from the wrong server without
            // having to reproduce the connection by hand.
            e.CanTrust = false;
            throw MeshAdapterPipelineExecutionException.SftpHostKeyMismatch(
                settings.Host, settings.HostKeyFingerprint!, e.FingerPrintSHA256);
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
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                client.Dispose();
            }
            finally
            {
                semaphore.Release();
            }
        }
    }
}
