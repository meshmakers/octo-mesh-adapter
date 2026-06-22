using System.Collections.Concurrent;
using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Renci.SshNet;
using Renci.SshNet.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that uploads a file via SFTP
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(SftpUploadNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpUploadNode(
    NodeDelegate next,
    IMeshEtlContext etlContext)
    : IPipelineNode
{
    private const string SftpSemaphoresKey = "SftpUploadNode.Semaphores";
    private static readonly Lock SemaphoresLock = new();

    // ReSharper disable once ClassNeverInstantiated.Local
    private record SftpServerConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string Host { get; init; }
        public int Port { get; init; } = 22;
        public required string Username { get; init; }
        public string? Password { get; init; }
        public string? PrivateKey { get; init; }
        public string? PrivateKeyPassphrase { get; init; }
        public int MaxConcurrentConnections { get; init; } = 3;
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpUploadNodeConfiguration>();

        try
        {
            ValidateConfiguration(c, nodeContext);

            if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
            {
                throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(nodeContext,
                    nameof(c.ServerConfiguration), c.ServerConfiguration);
            }

            var serverConfiguration =
                etlContext.GlobalConfiguration.GetValue<SftpServerConfiguration>(c.ServerConfiguration);

            ValidateAuthConfiguration(serverConfiguration, nodeContext);

            // Resolve file name
            var fileName = ResolveFileName(c, dataContext, nodeContext);

            // Build remote path
            var remotePath = c.RemoteDirectory.TrimEnd('/') + "/" + fileName;

            if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
            {
                nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SftpUpload, new
                {
                    host = serverConfiguration.Host,
                    port = serverConfiguration.Port,
                    username = serverConfiguration.Username,
                    remotePath,
                    fileName,
                    hasBinarySource = !string.IsNullOrWhiteSpace(c.FileRtId) ||
                                      !string.IsNullOrWhiteSpace(c.FileRtIdPath),
                    contentPath = c.Path
                });
                await next(dataContext, nodeContext);
                return;
            }

            var sftpSemaphore = GetOrCreateSemaphore(c.ServerConfiguration, serverConfiguration);

            // Get upload stream
            await using var uploadStream = await GetUploadStreamAsync(c, dataContext, nodeContext);

            // Connect and upload
            using var client = CreateSftpClient(serverConfiguration);

            await sftpSemaphore.WaitAsync();
            try
            {
                client.Connect();
                EnsureRemoteDirectoryExists(client, c.RemoteDirectory);
                client.UploadFile(uploadStream, remotePath, true);
            }
            finally
            {
                if (client.IsConnected)
                {
                    client.Disconnect();
                }

                sftpSemaphore.Release();
            }
        }
        catch (MeshAdapterPipelineExecutionException)
        {
            throw;
        }
        catch (Exception e)
        {
            throw MeshAdapterPipelineExecutionException.CannotUploadViaSftp(nodeContext, e);
        }

        await next(dataContext, nodeContext);
    }

    private static void ValidateConfiguration(SftpUploadNodeConfiguration c, INodeContext nodeContext)
    {
        // Validate that at least one file name source is configured
        if (string.IsNullOrWhiteSpace(c.FileName) && string.IsNullOrWhiteSpace(c.FileNamePath))
        {
            throw MeshAdapterPipelineExecutionException.FileNameNotConfigured(nodeContext);
        }

        // Validate that exactly one content source is configured
        var hasBinarySource = !string.IsNullOrWhiteSpace(c.FileRtId) ||
                              !string.IsNullOrWhiteSpace(c.FileRtIdPath);
        var hasStringSource = !string.IsNullOrWhiteSpace(c.Path);

        switch (hasBinarySource)
        {
            case true when hasStringSource:
                throw MeshAdapterPipelineExecutionException.AmbiguousFileSource(nodeContext);
            case false when !hasStringSource:
                throw MeshAdapterPipelineExecutionException.NoFileSourceSpecified(nodeContext);
        }
    }

    private static void ValidateAuthConfiguration(SftpServerConfiguration serverConfiguration,
        INodeContext nodeContext)
    {
        if (string.IsNullOrWhiteSpace(serverConfiguration.PrivateKey) &&
            string.IsNullOrWhiteSpace(serverConfiguration.Password))
        {
            throw MeshAdapterPipelineExecutionException.SftpAuthNotConfigured(nodeContext);
        }
    }

    private static string SanitizeFileName(string fileName, INodeContext nodeContext)
    {
        // Normalize both separators so traversal is blocked cross-platform
        // (Path.GetFileName only strips separators for the current OS)
        var normalized = fileName.Replace('\\', '/');
        var name = normalized.Split('/')[^1];

        if (string.IsNullOrWhiteSpace(name) || name == "." || name == "..")
        {
            throw MeshAdapterPipelineExecutionException.InvalidFileName(nodeContext, fileName);
        }

        return name;
    }

    private static string ResolveFileName(SftpUploadNodeConfiguration c, IDataContext dataContext,
        INodeContext nodeContext)
    {
        string? fileName;
        if (!string.IsNullOrWhiteSpace(c.FileNamePath))
        {
            fileName = dataContext.Get<string>(c.FileNamePath);
        }
        else
        {
            fileName = c.FileName;
        }

        if (string.IsNullOrWhiteSpace(fileName))
        {
            throw MeshAdapterPipelineExecutionException.FileNameNull(nodeContext, c.FileNamePath ?? c.FileName);
        }

        return SanitizeFileName(fileName, nodeContext);
    }

    private SemaphoreSlim GetOrCreateSemaphore(string serverConfigurationName,
        SftpServerConfiguration serverConfiguration)
    {
        lock (SemaphoresLock)
        {
            if (!etlContext.Properties.TryGetValue(SftpSemaphoresKey, out var semaphoresObj) ||
                semaphoresObj is not ConcurrentDictionary<string, SemaphoreSlim> semaphores)
            {
                semaphores = new ConcurrentDictionary<string, SemaphoreSlim>();
                etlContext.Properties[SftpSemaphoresKey] = semaphores;
            }

            return semaphores.GetOrAdd(serverConfigurationName,
                _ =>
                {
                    if (serverConfiguration.MaxConcurrentConnections <= 0)
                    {
                        throw MeshAdapterPipelineExecutionException.InvalidMaxConcurrentConnections(
                            serverConfigurationName, serverConfiguration.MaxConcurrentConnections);
                    }

                    return new SemaphoreSlim(
                        serverConfiguration.MaxConcurrentConnections,
                        serverConfiguration.MaxConcurrentConnections);
                });
        }
    }

    private async Task<Stream> GetUploadStreamAsync(
        SftpUploadNodeConfiguration configuration,
        IDataContext dataContext,
        INodeContext nodeContext)
    {
        // Binary file from MongoDB
        if (!string.IsNullOrWhiteSpace(configuration.FileRtIdPath) ||
            !string.IsNullOrWhiteSpace(configuration.FileRtId))
        {
            // Prefer dynamic value from data context when FileRtIdPath is configured
            string? fileRtId = null;

            if (!string.IsNullOrWhiteSpace(configuration.FileRtIdPath))
            {
                fileRtId = dataContext.Get<string>(configuration.FileRtIdPath);
            }

            // Fall back to static FileRtId only if no value was obtained from the path
            if (string.IsNullOrWhiteSpace(fileRtId) &&
                !string.IsNullOrWhiteSpace(configuration.FileRtId))
            {
                fileRtId = configuration.FileRtId;
            }

            if (string.IsNullOrWhiteSpace(fileRtId))
            {
                throw MeshAdapterPipelineExecutionException.RtIdValueNull(nodeContext, configuration.FileRtIdPath);
            }

            var tenantRepository = etlContext.TenantRepository;
            using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
            session.StartTransaction();

            var streamHandler = await tenantRepository.DownloadLargeBinaryAsync(session,
                OctoObjectId.Parse(fileRtId), CancellationToken.None);

            await session.CommitTransactionAsync().ConfigureAwait(false);

            if (streamHandler == null)
            {
                throw MeshAdapterPipelineExecutionException.BinaryNotFound(nodeContext, fileRtId);
            }

            return streamHandler.Stream;
        }

        // String content from data context
        var content = dataContext.Get<string>(configuration.Path);
        if (content == null)
        {
            throw PipelineExecutionException.ValueNotSet(nodeContext, configuration.Path);
        }

        return new MemoryStream(Encoding.UTF8.GetBytes(content));
    }

    private static SftpClient CreateSftpClient(SftpServerConfiguration serverConfiguration)
    {
        if (!string.IsNullOrWhiteSpace(serverConfiguration.PrivateKey))
        {
            var keyStream = new MemoryStream(Encoding.UTF8.GetBytes(serverConfiguration.PrivateKey));
            var privateKeyFile = string.IsNullOrWhiteSpace(serverConfiguration.PrivateKeyPassphrase)
                ? new PrivateKeyFile(keyStream)
                : new PrivateKeyFile(keyStream, serverConfiguration.PrivateKeyPassphrase);

            return new SftpClient(serverConfiguration.Host, serverConfiguration.Port,
                serverConfiguration.Username, [privateKeyFile]);
        }

        return new SftpClient(serverConfiguration.Host, serverConfiguration.Port,
            serverConfiguration.Username, serverConfiguration.Password ?? string.Empty);
    }

    private static void EnsureRemoteDirectoryExists(SftpClient client, string remotePath)
    {
        var isAbsolute = remotePath.StartsWith('/');
        var parts = remotePath.Split('/', StringSplitOptions.RemoveEmptyEntries);
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
}
