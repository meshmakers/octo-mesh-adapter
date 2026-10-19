using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
///     Lists the mails the e-mail document import gave up on (AB#7087) — those in the channel's failed
///     folder — for the accounting app's "Failed import mails" view. See
///     <see cref="ListFailedImportMailsNodeConfiguration" /> for the output. Read-only. The mailbox and
///     both folders come from the pipeline's stored configuration; nothing but the channel is taken from
///     the request. Every failure throws a message worded for the operator, without the node-path prefix,
///     like <c>ListMailFolders@1</c>.
/// </summary>
[NodeConfiguration(typeof(ListFailedImportMailsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ListFailedImportMailsNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    ILogger<ListFailedImportMailsNode> logger)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ListFailedImportMailsNodeConfiguration>();
        var channel = FailedImportMailsSupport.ResolveChannel(dataContext, c);
        var (max, timeoutSeconds) = FailedImportMailsSupport.ReadLimits(c, nodeContext);

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var cts = new CancellationTokenSource(timeout);

        JsonObject result;
        try
        {
            await using var mailbox = await FailedMailChannels.OpenAsync(channel, c, nodeContext,
                etlContext.GlobalConfiguration, httpClientFactory, timeout, logger, cts.Token);

            result = await BuildResultAsync(channel, mailbox, max, cts.Token);
        }
        catch (MailFolderListingException ex)
        {
            logger.LogWarning(ex, "ListFailedImportMails: reading the {Channel} failed folder failed", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel, ex.Message, ex);
        }
        catch (OperationCanceledException ex) when (cts.IsCancellationRequested)
        {
            logger.LogWarning(ex, "ListFailedImportMails: the {Channel} mailbox timed out", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel,
                $"Reading the failed folder did not finish within {timeoutSeconds} seconds.", ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MeshAdapterPipelineExecutionException)
        {
            // MailKit and HttpClient failures that no helper translated; the library text is the cause.
            logger.LogWarning(ex, "ListFailedImportMails: reading the {Channel} failed folder failed", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel,
                $"Reading the failed folder failed: {ex.Message}", ex);
        }

        nodeContext.Debug("ListFailedImportMails: {0} mail(s) listed for channel {1}",
            result["mails"]!.AsArray().Count, channel);
        dataContext.Set(c.TargetPath, (JsonNode)result, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    internal static async Task<JsonObject> BuildResultAsync(string channel, IFailedMailChannel mailbox, int max,
        CancellationToken cancellationToken)
    {
        var configured = !string.IsNullOrWhiteSpace(mailbox.FailedFolder);
        var listing = configured
            ? await mailbox.ListAsync(max, cancellationToken)
            : new FailedMailListing([], false);

        return ToJson(channel, mailbox, configured, listing);
    }

    internal static JsonObject ToJson(string channel, IFailedMailChannel mailbox, bool configured,
        FailedMailListing listing)
    {
        var mails = new JsonArray();
        foreach (var mail in listing.Mails)
        {
            mails.Add(new JsonObject
            {
                ["id"] = mail.Id,
                ["subject"] = mail.Subject,
                ["from"] = mail.From,
                ["receivedAt"] = mail.ReceivedAt?.UtcDateTime.ToString("O"),
                ["attempts"] = mail.Attempts,
                ["parked"] = mail.Parked,
                ["failureReason"] = mail.FailureReason
            });
        }

        return new JsonObject
        {
            ["channel"] = channel,
            ["mailbox"] = mailbox.Mailbox,
            ["configured"] = configured,
            ["folderFound"] = !configured || mailbox.FailedFolderFound,
            ["failedFolder"] = mailbox.FailedFolder,
            ["sourceFolder"] = mailbox.SourceFolder,
            ["truncated"] = listing.Truncated,
            ["mails"] = mails
        };
    }
}
