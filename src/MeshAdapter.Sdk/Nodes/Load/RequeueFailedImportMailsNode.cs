using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.MailFolders;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
///     Sends mails the import gave up on back into the folder it polls (AB#7087); see
///     <see cref="RequeueFailedImportMailsNodeConfiguration" />. Per mail the import markers (and for
///     IMAP the read/deleted flags) are cleared FIRST and the mail is moved only if that worked — a mail
///     moved back with its markers on is parked again by the next poll (AB#7068). One failing mail never
///     stops the others; the per-mail result is the node's output.
/// </summary>
[NodeConfiguration(typeof(RequeueFailedImportMailsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class RequeueFailedImportMailsNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory,
    ILogger<RequeueFailedImportMailsNode> logger)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<RequeueFailedImportMailsNodeConfiguration>();
        var channel = FailedImportMailsSupport.ResolveChannel(dataContext, c);
        var (max, timeoutSeconds) = FailedImportMailsSupport.ReadLimits(c, nodeContext);

        var all = !string.IsNullOrWhiteSpace(c.AllPath) && dataContext.Get<bool?>(c.AllPath) == true;
        var ids = all ? null : ReadIds(dataContext, c);
        if (ids is { Count: 0 })
        {
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel,
                "Nothing to re-queue: the request names no mail and does not ask for all of them.",
                new InvalidOperationException("empty selection"));
        }

        var timeout = TimeSpan.FromSeconds(timeoutSeconds);
        using var cts = new CancellationTokenSource(timeout);

        JsonObject result;
        try
        {
            await using var mailbox = await FailedMailChannels.OpenAsync(channel, c, nodeContext,
                etlContext.GlobalConfiguration, httpClientFactory, timeout, logger, cts.Token);

            result = await RunAsync(channel, mailbox, ids, max, logger, cts.Token);
        }
        catch (MailFolderListingException ex)
        {
            logger.LogWarning(ex, "RequeueFailedImportMails: the {Channel} mailbox could not be used", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel, ex.Message, ex);
        }
        catch (Exception ex) when (ex is not OperationCanceledException and not MeshAdapterPipelineExecutionException)
        {
            logger.LogWarning(ex, "RequeueFailedImportMails: the {Channel} mailbox could not be used", channel);
            throw MeshAdapterPipelineExecutionException.MailFolderListingFailed(channel,
                $"Re-queueing failed: {ex.Message}", ex);
        }

        nodeContext.Debug("RequeueFailedImportMails: {0} requeued, {1} not found, {2} failed on channel {3}",
            result["requeued"]!.GetValue<int>(), result["notFound"]!.GetValue<int>(),
            result["failed"]!.GetValue<int>(), channel);
        dataContext.Set(c.TargetPath, (JsonNode)result, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    ///     Re-queues <paramref name="ids" /> (null = every mail of the failed folder, capped at
    ///     <paramref name="max" />). Each mail is its own failure domain; running out of the time budget
    ///     marks the rest as failed instead of aborting the response.
    /// </summary>
    internal static async Task<JsonObject> RunAsync(string channel, IFailedMailChannel mailbox,
        IReadOnlyList<string>? ids, int max, ILogger logger, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(mailbox.FailedFolder))
        {
            throw new MailFolderListingException("No failed folder is configured — set one in Settings -> Import.");
        }

        var truncated = false;
        List<string> selected;
        var subjects = new Dictionary<string, string?>();
        if (ids == null)
        {
            var listing = await mailbox.ListAsync(max, cancellationToken);
            selected = listing.Mails.Select(m => m.Id).ToList();
            subjects = listing.Mails.ToDictionary(m => m.Id, m => m.Subject);
            truncated = listing.Truncated;
        }
        else
        {
            selected = ids.Distinct(StringComparer.Ordinal).ToList();
            if (selected.Count > max)
            {
                selected = selected.Take(max).ToList();
                truncated = true;
            }
        }

        var outcomes = new List<RequeueOutcome>();
        if (selected.Count > 0)
        {
            await mailbox.PrepareRequeueAsync(cancellationToken);
        }

        foreach (var id in selected)
        {
            try
            {
                outcomes.Add(await mailbox.RequeueAsync(id, cancellationToken));
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                outcomes.Add(RequeueOutcome.Error(id, subjects.GetValueOrDefault(id),
                    "Not processed: the time budget of this request ran out. Try again."));
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "RequeueFailedImportMails: re-queueing one {Channel} mail failed", channel);
                outcomes.Add(RequeueOutcome.Error(id, subjects.GetValueOrDefault(id),
                    ex is MailFolderListingException ? ex.Message : $"Unexpected error: {ex.Message}"));
            }
        }

        return ToJson(channel, mailbox, selected.Count, outcomes, truncated, subjects);
    }

    internal static JsonObject ToJson(string channel, IFailedMailChannel mailbox, int requested,
        IReadOnlyList<RequeueOutcome> outcomes, bool truncated, IReadOnlyDictionary<string, string?> knownSubjects)
    {
        var results = new JsonArray();
        foreach (var outcome in outcomes)
        {
            results.Add(new JsonObject
            {
                ["id"] = outcome.Id,
                ["subject"] = outcome.Subject ?? knownSubjects.GetValueOrDefault(outcome.Id),
                ["status"] = outcome.Status,
                ["reason"] = outcome.Reason,
                ["internetMessageId"] = outcome.InternetMessageId
            });
        }

        return new JsonObject
        {
            ["channel"] = channel,
            ["mailbox"] = mailbox.Mailbox,
            ["failedFolder"] = mailbox.FailedFolder,
            ["sourceFolder"] = mailbox.SourceFolder,
            ["requested"] = requested,
            ["requeued"] = outcomes.Count(o => o.Status == RequeueOutcome.Requeued),
            ["notFound"] = outcomes.Count(o => o.Status == RequeueOutcome.NotFound),
            ["failed"] = outcomes.Count(o => o.Status == RequeueOutcome.Failed),
            ["truncated"] = truncated,
            ["results"] = results
        };
    }

    private static List<string> ReadIds(IDataContext dataContext, RequeueFailedImportMailsNodeConfiguration c)
    {
        if (string.IsNullOrWhiteSpace(c.MessageIdsPath))
        {
            return [];
        }

        var array = dataContext.Get<JsonArray>(c.MessageIdsPath);
        if (array == null)
        {
            return [];
        }

        return array
            .Select(n => n is JsonValue v && v.TryGetValue<string>(out var s) ? s : null)
            .Where(s => !string.IsNullOrWhiteSpace(s))
            .Select(s => s!)
            .ToList();
    }
}
