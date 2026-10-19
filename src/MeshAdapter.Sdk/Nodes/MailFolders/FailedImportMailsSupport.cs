using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;

/// <summary>What <c>ListFailedImportMails@1</c> and <c>RequeueFailedImportMails@1</c> read the same way.</summary>
internal static class FailedImportMailsSupport
{
    /// <summary>The channel: <c>channelPath</c> when it resolves, else <c>channel</c>; anything but Imap/Graph is refused.</summary>
    internal static string ResolveChannel(IDataContext dataContext, FailedImportMailsNodeConfiguration c)
    {
        string? raw = null;
        if (!string.IsNullOrWhiteSpace(c.ChannelPath))
        {
            raw = dataContext.Get<string>(c.ChannelPath);
        }

        if (string.IsNullOrWhiteSpace(raw))
        {
            raw = c.Channel;
        }

        return ListMailFoldersNode.NormalizeChannel(raw) ??
               throw MeshAdapterPipelineExecutionException.MailFolderChannelInvalid(raw);
    }

    internal static (int MaxMails, int TimeoutSeconds) ReadLimits(FailedImportMailsNodeConfiguration c,
        INodeContext nodeContext)
    {
        var max = c.MaxMails ?? FailedImportMailsNodeConfiguration.DefaultMaxMails;
        if (max <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, $"maxMails must be positive, got {max}");
        }

        var timeout = c.TimeoutSeconds ?? FailedImportMailsNodeConfiguration.DefaultTimeoutSeconds;
        if (timeout <= 0)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                $"timeoutSeconds must be positive, got {timeout}");
        }

        return (max, timeout);
    }
}
