using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Downloads one file from an SFTP server and writes its decoded content to the target path.
/// Read counterpart of <c>SftpUpload@1</c>, which writes exactly one file. Meant to run inside
/// a <c>ForEach@1</c> over an <c>SftpList@1</c> result, one session per file.
/// <para />
/// Reading has no side effects, so there is no dry-run branch: the downstream chain must see
/// the content in a dry run as well.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
/// <param name="sessionFactory">Opens the SFTP session, including the concurrency limit and host key check</param>
[NodeConfiguration(typeof(SftpDownloadNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpDownloadNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpDownloadNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.RemotePath) && string.IsNullOrWhiteSpace(c.RemotePathPath))
        {
            throw MeshAdapterPipelineExecutionException.NoRemotePathSpecified(nodeContext);
        }

        var remotePath = string.IsNullOrWhiteSpace(c.RemotePathPath)
            ? c.RemotePath
            : dataContext.Get<string>(c.RemotePathPath);

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            throw PipelineExecutionException.ValueNotSet(nodeContext, c.RemotePathPath ?? nameof(c.RemotePath));
        }

        var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);

        byte[] content;
        using (var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration, etlContext))
        {
            content = session.Download(remotePath);
        }

        var text = SftpContentDecoder.Decode(content, c.Encoding, c.OnEncodingError, nodeContext);

        dataContext.Set(c.TargetPath, text, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        nodeContext.Debug("SftpDownload: read {0} byte(s) from '{1}'", content.Length, remotePath);

        await next(dataContext, nodeContext);
    }
}
