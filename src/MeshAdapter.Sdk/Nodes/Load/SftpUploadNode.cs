using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that uploads a file via SFTP
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
/// <param name="sessionFactory">Opens the SFTP session, including the concurrency limit and host key check</param>
[NodeConfiguration(typeof(SftpUploadNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpUploadNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpUploadNodeConfiguration>();

        try
        {
            ValidateConfiguration(c, nodeContext);

            var serverConfiguration = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration,
                nodeContext);

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
                    contentPath = c.Path,
                    encoding = c.Encoding,
                    onEncodingError = c.OnEncodingError.ToString()
                });
            }
            else
            {
                // Get upload stream
                await using var uploadStream = await GetUploadStreamAsync(c, dataContext, nodeContext);

                // Connect and upload. The session holds the server's concurrency slot until it
                // is disposed, so it stays in a using scope.
                using var session = await sessionFactory.ConnectAsync(serverConfiguration, c.ServerConfiguration,
                    etlContext, nodeContext);
                session.EnsureDirectory(c.RemoteDirectory);
                session.Upload(uploadStream, remotePath);
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

        // Outside the try, and reached from both branches: the catch above speaks for the
        // upload, so a failure further down the chain must not come back as "Cannot upload
        // file via SFTP". A dry run took the other branch and wrote nothing, but the chain
        // still has to see the run through.
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

    internal async Task<Stream> GetUploadStreamAsync(
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
            // AB#5028 — SYSTEM by decision: binary download for an outgoing channel, same rule as
            // SendEMail@1 / ToDiscord@1 — the file being shipped is regularly somebody else's.
            using var session = await etlContext.GetSystemSessionAsync().ConfigureAwait(false);
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

        return new MemoryStream(SftpContentEncoder.Encode(content, configuration.Encoding,
            configuration.OnEncodingError, nodeContext));
    }

}
