using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Deletes one file from an SFTP server: it removes the file <c>SftpDownload@1</c> read.
/// Meant to run inside a <c>ForEach@1</c> over an <c>SftpList@1</c> result, one session per
/// file.
/// <para />
/// A file that is not there any more is not automatically a failure: the goal state is that it
/// is gone. Which of the two a run should show is stated per pipeline through
/// <c>onMissingFile</c>, and the strict reading is the default. The tolerance covers what
/// the server reports as a missing path; a server that answers a doomed delete with a generic
/// failure instead is not distinguishable here and surfaces as a delete failure.
/// </summary>
/// <param name="next">Next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
/// <param name="sessionFactory">Opens the SFTP session, including the concurrency limit and host key check</param>
[NodeConfiguration(typeof(SftpDeleteNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SftpDeleteNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISftpSessionFactory sessionFactory)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SftpDeleteNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.RemotePath) && string.IsNullOrWhiteSpace(c.RemotePathPath))
        {
            throw MeshAdapterPipelineExecutionException.NoRemotePathSpecified(nodeContext);
        }

        var remotePath = string.IsNullOrWhiteSpace(c.RemotePathPath)
            ? c.RemotePath
            : dataContext.Get<string>(c.RemotePathPath);

        if (string.IsNullOrWhiteSpace(remotePath))
        {
            // Only reachable with RemotePathPath configured: the guard above already refused a
            // definition where both sources are blank, so the path named here is the one that
            // resolved to nothing.
            throw PipelineExecutionException.ValueNotSet(nodeContext, c.RemotePathPath!);
        }

        if (remotePath.EndsWith('/'))
        {
            // A directory is not a target for this node. Refused here rather than at the
            // server, whose answer says nothing about the actual mistake: which status a
            // doomed delete comes back with depends on the implementation, and the ones in
            // use read the same as a missing file or a plain refusal.
            throw MeshAdapterPipelineExecutionException.RemotePathIsDirectory(nodeContext, remotePath);
        }

        var settings = SftpServerSettingsResolver.Resolve(etlContext, c.ServerConfiguration, nodeContext);

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SftpDelete, new
            {
                host = settings.Host,
                port = settings.Port,
                username = settings.Username,
                remotePath,
                onMissingFile = c.OnMissingFile.ToString()
            });
        }
        else
        {
            bool deleted;
            try
            {
                // The session holds the server's concurrency slot until it is disposed, so it
                // stays in a using scope.
                using var session = await sessionFactory.ConnectAsync(settings, c.ServerConfiguration, etlContext,
                    nodeContext);
                deleted = session.Delete(remotePath);
            }
            catch (MeshAdapterPipelineExecutionException)
            {
                // A host key mismatch or an exhausted slot wait already names this node and the
                // server configuration. Wrapping it again would bury that under a transport
                // message.
                throw;
            }
            catch (Exception e)
            {
                // Name the node, the way the sibling upload node does. A bare SSH.NET message
                // leaves whoever reads the run guessing which step it came from.
                throw MeshAdapterPipelineExecutionException.CannotDeleteViaSftp(nodeContext, remotePath, e);
            }

            // Outside the try: a file that was already gone is not a transport failure and
            // must not be reported as one, and Dispose has released the connection slot
            // before the decision is taken.
            if (deleted)
            {
                nodeContext.Info("SftpDelete: removed '{0}'", remotePath);
            }
            else if (c.OnMissingFile == MissingFileHandling.Ignore)
            {
                nodeContext.Warning("SftpDelete: '{0}' does not exist any more, nothing to delete", remotePath);
            }
            else
            {
                throw MeshAdapterPipelineExecutionException.SftpFileNotFound(nodeContext, remotePath);
            }
        }

        // Outside the try, and reached from both branches: the catch above speaks for the
        // deletion, so a failure further down the chain must not come back as "Cannot delete
        // file via SFTP". A dry run took the other branch and removed nothing, but the chain
        // still has to see the run through.
        await next(dataContext, nodeContext);
    }
}
