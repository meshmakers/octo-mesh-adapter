using System.Net;
using System.Net.Mail;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using MailKit;
using MailKit.Net.Imap;
using MailKit.Search;
using MailKit.Security;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;
using MimeKit;

using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromEmailNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromEmailNode(ILogger<FromEmailNode> logger, IChannelCallerBinder callerBinder)
    : ITriggerPipelineNode
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;
    private ImapClient? _imapClient;

    /// <summary>
    ///     AB#5337: the "no successPath configured, so nothing is flagged" warning is worth saying
    ///     once per process rather than once per polling interval.
    /// </summary>
    private bool _successPathWarningLogged;
    
    // ReSharper disable once ClassNeverInstantiated.Local
    private record EmailServerConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string Host { get; init; }
        public required int Port { get; init; }
        public required string Username { get; init; }
        public required string Password { get; init; }
        public required bool IsSslEnabled { get; init; }
        public string Folder { get; init; } = "INBOX";
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    public async Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromEmailNodeConfiguration>();
        
        if (!context.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext,
                nameof(c.ServerConfiguration),
                c.ServerConfiguration);
        }

        // AB#5345: the settings entity has the last word, so validate what will actually be USED,
        // not what the definition happens to say.
        var effective = ResolveEffectiveConfiguration(context.GlobalConfiguration, c);

        // AB#5337: a confirmation path that cannot name exactly one value is a configuration
        // mistake, and it has to be heard when the pipeline is deployed rather than be read as
        // "never confirmed" on every poll for the rest of the adapter's life.
        if (!string.IsNullOrWhiteSpace(effective.SuccessPath) &&
            !MailSuccessPath.TryParse(effective.SuccessPath, out _))
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(context.NodeContext, effective.SuccessPath);
        }

        if (!string.IsNullOrWhiteSpace(c.SettingsConfiguration))
        {
            logger.LogInformation(
                "FromEmail: runtime settings resolved from configuration '{Settings}' " +
                "(folder='{Folder}', postProcessing={Mode}, interval={Interval}s, onlyUnread={OnlyUnread})",
                c.SettingsConfiguration, effective.SourceFolder, ResolveEffectivePostProcessingMode(effective),
                effective.PollingIntervalSeconds, effective.OnlyUnread);
        }

        var serverConfig = context.GlobalConfiguration.GetValue<EmailServerConfiguration>(c.ServerConfiguration);
        
        _cancellationTokenSource = new CancellationTokenSource();
        _imapClient = new ImapClient();
        
        // Connect and authenticate
        await ConnectAndAuthenticateAsync(serverConfig);
        
        // Start polling task
        _pollingTask = Task.Run(async () => await PollForEmailsAsync(context, serverConfig, c), _cancellationTokenSource.Token);
    }

    /// <summary>
    ///     Returns a copy of <paramref name="c" /> with every RUNTIME setting resolved from the
    ///     optional settings configuration (well-known name
    ///     <see cref="FromEmailNodeConfiguration.SettingsConfiguration" />), AB#5345.
    ///     <para>
    ///     One rule, and it is also the migration path: <b>a value found in the settings wins, the
    ///     node property is the fallback.</b> A pipeline that names no settings configuration, or
    ///     whose settings entity is absent, undefined or malformed, is returned unchanged and
    ///     therefore behaves exactly as it did before this existed.
    ///     </para>
    ///     <para>
    ///     Connection details are deliberately NOT here: host, port, user, password, TLS and the
    ///     default folder have always lived on the <c>System.Communication/EMailReceiverConfiguration</c>
    ///     entity that <see cref="FromEmailNodeConfiguration.ServerConfiguration" /> names — the
    ///     definition only ever carried a pointer to them.
    ///     </para>
    /// </summary>
    internal static FromEmailNodeConfiguration ResolveEffectiveConfiguration(
        IGlobalConfiguration globalConfiguration, FromEmailNodeConfiguration c)
    {
        var attributes = ConfigurationSettingsReader.TryGetAttributes(
            globalConfiguration, c.SettingsConfiguration);
        if (attributes is null)
        {
            return c;
        }

        return c with
        {
            PollingIntervalSeconds =
                ConfigurationSettingsReader.ReadPositiveInt(attributes, c.PollingSecondsAttribute)
                ?? c.PollingIntervalSeconds,
            OnlyUnread = ConfigurationSettingsReader.ReadBool(attributes, c.OnlyUnreadAttribute)
                         ?? c.OnlyUnread,
            PostProcessingMode =
                ConfigurationSettingsReader.ReadEnum<MailPostProcessingMode>(
                    attributes, c.PostProcessingModeAttribute) ?? c.PostProcessingMode,
            SourceFolder = ConfigurationSettingsReader.ReadString(attributes, c.SourceFolderAttribute)
                           ?? c.SourceFolder,
            DoneFolder = ConfigurationSettingsReader.ReadString(attributes, c.DoneFolderAttribute)
                         ?? c.DoneFolder,
            FailedFolder = ConfigurationSettingsReader.ReadString(attributes, c.FailedFolderAttribute)
                           ?? c.FailedFolder,
            // Zero-tolerant on purpose: 0 is this property's documented "no limit" opt-out, so it
            // has to survive the trip through the settings entity instead of reading as "unset".
            MaxMessagesPerPoll =
                ConfigurationSettingsReader.ReadInt(attributes, c.MaxMessagesPerPollAttribute)
                ?? c.MaxMessagesPerPoll,
            SinceDate = ConfigurationSettingsReader.ReadDateTime(attributes, c.SinceDateAttribute)
                        ?? c.SinceDate,
            SinceDaysBack = ConfigurationSettingsReader.ReadInt(attributes, c.SinceDaysBackAttribute)
                            ?? c.SinceDaysBack,
            SenderFilter = ConfigurationSettingsReader.ReadString(attributes, c.SenderFilterAttribute)
                           ?? c.SenderFilter,
            SubjectFilter = ConfigurationSettingsReader.ReadString(attributes, c.SubjectFilterAttribute)
                            ?? c.SubjectFilter,
            SuccessPath = ConfigurationSettingsReader.ReadString(attributes, c.SuccessPathAttribute)
                          ?? c.SuccessPath,
        };
    }

    private async Task ConnectAndAuthenticateAsync(EmailServerConfiguration config)
    {
        try
        {
            if (config.IsSslEnabled)
            {
                await _imapClient!.ConnectAsync(config.Host, config.Port, SecureSocketOptions.SslOnConnect);
            }
            else
            {
                await _imapClient!.ConnectAsync(config.Host, config.Port, SecureSocketOptions.StartTlsWhenAvailable);
            }
            
            await _imapClient.AuthenticateAsync(config.Username, config.Password);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Failed to connect to IMAP server");
            throw;
        }
    }

    private async Task PollForEmailsAsync(ITriggerContext context, EmailServerConfiguration serverConfig,
        FromEmailNodeConfiguration definitionConfig)
    {
        var processedUids = new HashSet<UniqueId>();
        
        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                // AB#5345: the settings are resolved HERE, once per poll, not once in StartAsync —
                // a setting is operating state and the node must read it where it uses it, not
                // freeze a copy of it for the lifetime of the trigger.
                var nodeConfig = ResolveEffectiveConfiguration(context.GlobalConfiguration, definitionConfig);

                // Ensure we're connected
                if (!_imapClient!.IsConnected)
                {
                    await ConnectAndAuthenticateAsync(serverConfig);
                }
                
                // Open the folder. The polled folder is the connection entity's `Folder` unless the
                // settings/definition name one — which they do wherever the three-folder mode needs
                // a source that belongs to the same triple as done and failed (AB#5345).
                var folder = await _imapClient.GetFolderAsync(ResolveSourceFolderName(nodeConfig, serverConfig));
                await folder.OpenAsync(FolderAccess.ReadWrite);
                
                // Search for messages. The date cut-off is applied SERVER-SIDE (AB#5340) so a mailbox
                // with years of history never reaches the client in the first place.
                var uids = await folder.SearchAsync(BuildSearchQuery(nodeConfig));

                // AB#5336: never fetch the whole search result. Every message below is downloaded in full
                // and its attachments are base64-encoded into the batch, so an unbounded result set is an
                // out-of-memory failure (1697 mails killed the adapter on prod-1/gastroacker). Take at most
                // MaxMessagesPerPoll per pass and let a backlog drain over consecutive polls.
                var pendingUids = uids.Where(uid => !processedUids.Contains(uid)).ToList();
                var batchUids = ApplyBatchCap(pendingUids, ResolveMaxMessagesPerPoll(nodeConfig));

                if (batchUids.Count < pendingUids.Count)
                {
                    logger.LogInformation(
                        "FromEmail: taking {Taken} of {Pending} pending message(s) this poll; the remainder follows on the next poll(s).",
                        batchUids.Count, pendingUids.Count);
                }

                // AB#5339: the server's INTERNALDATE for this batch, the fallback receive time for a
                // mail whose `Date:` header is missing or unparsable. Fetched for the whole batch in
                // one round trip, before any message is downloaded — a failure here is a connection
                // problem that the downloads below would hit anyway, so it bubbles to the poll
                // handler rather than being softened into "no fallback available".
                var internalDates = await FetchInternalDatesAsync(folder, batchUids);

                // Process new emails
                var newEmails = new List<EmailData>();

                // AB#5337: the UIDs that actually made it into the batch. Messages the client-side
                // filters below rejected are deliberately absent — they were never processed, so
                // nothing may be written back to the server for them.
                var batchedUids = new List<UniqueId>();
                foreach (var uid in batchUids)
                {
                    var message = await folder.GetMessageAsync(uid);

                    // Mark the UID as examined BEFORE the client-side filters below (AB#5336). The
                    // filters skip with `continue`, so a message they reject never reached the
                    // bookkeeping further down and is offered again on the next poll. Without a cap
                    // that was merely wasteful; with one, rejected messages occupy the budget on every
                    // pass and starve everything behind them indefinitely.
                    processedUids.Add(uid);

                    // Apply sender filter if specified
                    if (!string.IsNullOrWhiteSpace(nodeConfig.SenderFilter))
                    {
                        var senderAddress = message.From?.Mailboxes?.FirstOrDefault()?.Address;
                        if (senderAddress == null || !senderAddress.Contains(nodeConfig.SenderFilter))
                            continue;
                    }
                    
                    // Apply subject filter if specified
                    if (!string.IsNullOrWhiteSpace(nodeConfig.SubjectFilter))
                    {
                        if (message.Subject == null || !message.Subject.Contains(nodeConfig.SubjectFilter))
                            continue;
                    }
                    
                    var emailData = new EmailData
                    {
                        Subject = message.Subject,
                        From = message.From?.ToString(),
                        FromAddress = message.From?.Mailboxes?.FirstOrDefault()?.Address,
                        To = message.To?.ToString(),
                        // AB#5339: never write year 1 — see ResolveReceivedAt.
                        Date = ResolveReceivedAt(message.Date,
                            internalDates.TryGetValue(uid, out var internalDate) ? internalDate : null),
                        Body = message.TextBody ?? message.HtmlBody,
                        HtmlBody = message.HtmlBody,
                        TextBody = message.TextBody,
                        MessageId = message.MessageId,
                        Attachments = message.Attachments?.Select(a =>
                        {
                            var attachment = new AttachmentData
                            {
                                FileName = a.ContentDisposition?.FileName ?? "unknown",
                                ContentType = a.ContentType?.MimeType ?? "application/octet-stream"
                            };

                            if (a is MimePart { Content: not null } mimePart)
                            {
                                using var memoryStream = new MemoryStream();
                                mimePart.Content.DecodeTo(memoryStream);
                                attachment.Data = Convert.ToBase64String(memoryStream.ToArray());
                                attachment.Length = memoryStream.Length;
                            }

                            // AB#5338: many senders declare a PDF as application/octet-stream. The
                            // shared Stage Document pipeline keys on the DECLARED type and routes
                            // anything but application/pdf through its image->PDF branch, which
                            // REPLACES the stored bytes with a blank render — a 90 KB invoice became
                            // a 5.6 KB empty page on prod-1/gastroacker. Correct the type from the
                            // content, as the Graph channel has done since AB#4433.
                            attachment.ContentType = AttachmentContentType.NormalizePdf(
                                attachment.FileName, attachment.ContentType, attachment.Data);

                            return attachment;
                        }).ToList() ?? new List<AttachmentData>()
                    };
                    
                    // AB#5125: surface the receiving server's Authentication-Results (DKIM/DMARC)
                    // verdict so the caller-binding can derive this mail's message trust.
                    PopulateAuthentication(emailData, message);

                    newEmails.Add(emailData);
                    batchedUids.Add(uid);

                    // AB#5337: NOTHING is flagged here. `\Seen` and `\Deleted` are written back only
                    // after the pipeline ran and succeeded — see below.
                }

                // AB#5337: gate for the server-side write-back further down. It stays false
                // unless the run came back — a throw skips straight past the assignment — and, where
                // a successPath is configured, unless the pipeline also confirmed the import.
                var writeBackAllowed = false;

                // Trigger the pipeline if we have new emails
                if (newEmails.Count > 0)
                {
                    var emailBatch = new EmailBatch
                    {
                        Emails = newEmails,
                        Count = newEmails.Count,
                        ProcessedAt = DateTime.UtcNow
                    };
                    
                    // AB#5126: one execution per batch. A caller is only unambiguous when the whole
                    // batch shares one sender address; otherwise the execution has no single identity.
                    // The From address is the identifier; AB#5125 derives the per-message trust from
                    // each mail's DKIM/DMARC (Authentication-Results) verdict and takes the WEAKEST
                    // over the batch (fail-safe: one unauthenticated mail caps the batch at Weak).
                    var distinctSenders = newEmails
                        .Select(e => e.FromAddress)
                        .Where(a => !string.IsNullOrWhiteSpace(a))
                        .Distinct()
                        .ToList();
                    var messageTrust = EmailMessageTrust.Min(
                        newEmails.Select(e => EmailMessageTrust.Evaluate(e.Authentication, e.FromAddress)));
                    var sender = distinctSenders.Count == 1
                        ? new ChannelSender(ChannelIdentifierKind.EmailAddress, distinctSenders[0]!, messageTrust)
                        : null;
                    var binding = await callerBinder.BindAsync(context.TenantId, nodeConfig.CallerBinding, sender);
                    if (binding.Rejected)
                    {
                        // Not an execution: the batch was never handed to the pipeline, so the mails
                        // keep their flags and an operator who repairs the binding still has them.
                        logger.LogWarning("FromEmail: {Reason} Skipping batch of {Count} email(s).",
                            binding.RejectReason, newEmails.Count);
                    }
                    else
                    {
                        try
                        {
                            var executionResult = await context.ExecuteAsync(
                                new ExecutePipelineOptions(DateTime.UtcNow)
                                {
                                    VerifiedPrincipal = binding.Principal,
                                    CallerTrust = binding.Trust
                                }, emailBatch);

                            // AB#5337: returning is not the same as importing, so where the
                            // pipeline was asked to confirm, its confirmation decides.
                            var confirmation =
                                MailSuccessPath.Evaluate(nodeConfig.SuccessPath, executionResult as JsonNode);
                            writeBackAllowed = MailSuccessPath.IsPostProcessingAllowed(confirmation);

                            if (confirmation == MailRunConfirmation.NotConfirmed)
                            {
                                logger.LogWarning(
                                    "FromEmail: the pipeline run for {Count} mail(s) ended without setting " +
                                    "'{SuccessPath}' to true — a node may have reported an error and stopped its " +
                                    "branch while the execution still completed. The mails keep their server-side " +
                                    "flags so they are not lost.",
                                    newEmails.Count, nodeConfig.SuccessPath);
                            }
                            else
                            {
                                if (confirmation == MailRunConfirmation.NotConfigured &&
                                    !_successPathWarningLogged)
                                {
                                    _successPathWarningLogged = true;
                                    logger.LogWarning(
                                        "FromEmail: no 'successPath' is configured, so a batch whose import branch " +
                                        "stopped on a reported node error cannot be told apart from an imported " +
                                        "one, and the mails are flagged either way. Configure 'successPath' and " +
                                        "have the pipeline write that flag as the last step of its import branch " +
                                        "to make the write-back conditional on the import itself.");
                                }

                                logger.LogInformation("Processed {Count} new emails", newEmails.Count);
                            }
                        }
                        catch (OperationCanceledException) when (_cancellationTokenSource.Token
                                                                     .IsCancellationRequested)
                        {
                            // Adapter shutdown, not a batch failure — unwind to the poll loop's
                            // cancellation handling without touching the mailbox.
                            throw;
                        }
                        catch (Exception ex)
                        {
                            // AB#5337: swallowed on purpose so the poll finishes cleanly WITHOUT the
                            // write-back below. The mails keep their server-side state, which is the
                            // only durable record that they were never imported.
                            logger.LogError(ex,
                                "FromEmail: the pipeline run failed for a batch of {Count} mail(s); " +
                                "their server-side flags are left untouched so the mails are not lost.",
                                newEmails.Count);
                        }
                    }
                }

                // AB#5337: the server-side bookkeeping happens HERE, after the run, and never for
                // a run that threw — `writeBackAllowed` is still false then, because the assignment
                // sits behind the await. Doing it inside the UID loop above destroyed a receipt on
                // every failure: the mail was already `\Seen`, the default `onlyUnread: true` search
                // never offers it again, and nothing about the missing document is visible to
                // anyone.
                //
                // A failed batch is NOT retried by the next poll of this process: `processedUids`
                // (AB#5336) already holds these UIDs, which is what keeps a permanently failing mail
                // from being re-downloaded and re-run every polling interval. That set is
                // process-local, so a restart does offer the mail again — deliberately: the mail is
                // still there, unread and unflagged, and that is the property this fix buys.
                if (batchedUids.Count > 0)
                {
                    await ApplyPostProcessingAsync(folder, batchedUids, nodeConfig, writeBackAllowed);
                }

                await folder.CloseAsync();
                
                // Wait for the polling interval
                await Task.Delay(TimeSpan.FromSeconds(nodeConfig.PollingIntervalSeconds), _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                // Expected when cancellation is requested
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while polling for emails");
                
                // Wait before retrying
                await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
            }
        }
    }

    public async Task StopAsync(ITriggerContext context)
    {
        _cancellationTokenSource?.Cancel();
        
        if (_pollingTask != null)
        {
            try
            {
                await _pollingTask.WaitAsync(TimeSpan.FromSeconds(10));
            }
            catch (TimeoutException)
            {
                logger.LogWarning("Email polling task did not complete within timeout");
            }
        }
        
        if (_imapClient?.IsConnected == true)
        {
            await _imapClient.DisconnectAsync(true);
        }
        
        _imapClient?.Dispose();
        _cancellationTokenSource?.Dispose();
    }

    /// <summary>
    ///     Builds the IMAP search predicate. The date cut-off (AB#5340) is ANDed onto the read-state
    ///     predicate so the server, not the client, discards everything outside the window.
    /// </summary>
    internal static SearchQuery BuildSearchQuery(FromEmailNodeConfiguration nodeConfig)
    {
        var query = nodeConfig.OnlyUnread ? SearchQuery.NotSeen : SearchQuery.All;
        var since = ResolveSinceDate(nodeConfig);

        return since.HasValue
            ? SearchQuery.And(query, SearchQuery.DeliveredAfter(since.Value))
            : query;
    }

    /// <summary>
    ///     Effective per-poll cap: the configured value, or
    ///     <see cref="FromEmailNodeConfiguration.DefaultMaxMessagesPerPoll" /> when none is set. An
    ///     explicit null must not read as 0 — see the remark on the property.
    /// </summary>
    internal static int ResolveMaxMessagesPerPoll(FromEmailNodeConfiguration nodeConfig)
    {
        return nodeConfig.MaxMessagesPerPoll ?? FromEmailNodeConfiguration.DefaultMaxMessagesPerPoll;
    }

    /// <summary>
    ///     Caps one polling pass at <paramref name="maxMessagesPerPoll" /> messages (AB#5336). A value
    ///     &lt;= 0 disables the cap and restores the unbounded pre-fix behaviour.
    /// </summary>
    internal static List<UniqueId> ApplyBatchCap(List<UniqueId> pendingUids, int maxMessagesPerPoll)
    {
        return maxMessagesPerPoll > 0 && pendingUids.Count > maxMessagesPerPoll
            ? pendingUids.Take(maxMessagesPerPoll).ToList()
            : pendingUids;
    }

    /// <summary>
    ///     Resolves the effective IMAP <c>SINCE</c> cut-off (AB#5340).
    ///     <see cref="FromEmailNodeConfiguration.SinceDate" /> wins when both are configured;
    ///     <see cref="FromEmailNodeConfiguration.SinceDaysBack" /> is the relative fallback. Returns
    ///     <c>null</c> when neither is set, which keeps the previous unbounded behaviour.
    /// </summary>
    internal static DateTime? ResolveSinceDate(FromEmailNodeConfiguration nodeConfig)
    {
        if (nodeConfig.SinceDate.HasValue)
        {
            return nodeConfig.SinceDate.Value.Date;
        }

        if (nodeConfig.SinceDaysBack is > 0)
        {
            return DateTime.UtcNow.Date.AddDays(-nodeConfig.SinceDaysBack.Value);
        }

        return null;
    }

    /// <summary>
    ///     Reads the IMAP <c>INTERNALDATE</c> of a batch in one FETCH (AB#5339). Only messages the
    ///     server actually reports a value for appear in the result.
    /// </summary>
    private static async Task<Dictionary<UniqueId, DateTimeOffset>> FetchInternalDatesAsync(
        IMailFolder folder, IList<UniqueId> uids)
    {
        if (uids.Count == 0)
        {
            return new Dictionary<UniqueId, DateTimeOffset>();
        }

        var summaries = await folder.FetchAsync(uids,
            MessageSummaryItems.UniqueId | MessageSummaryItems.InternalDate);

        var internalDates = new Dictionary<UniqueId, DateTimeOffset>(summaries.Count);
        foreach (var summary in summaries)
        {
            if (summary.InternalDate.HasValue)
            {
                internalDates[summary.UniqueId] = summary.InternalDate.Value;
            }
        }

        return internalDates;
    }

    /// <summary>
    ///     Resolves when the mail was received (AB#5339). The <c>Date:</c> header wins when it is
    ///     there and parsable; MimeKit reports both a MISSING and an unparsable header as
    ///     <see cref="DateTimeOffset.MinValue" />, and writing that straight through produced
    ///     <c>sourceReceivedAt = 0001-01-01T00:00:00Z</c> on the staged document — a timestamp that
    ///     sorts to the very front of the inbox and falls outside every fiscal year, so the receipt
    ///     cannot be assigned at all (prod-1/gastroacker: 67 mails from one sender that emits no
    ///     <c>Date:</c> header).
    ///     <para>
    ///     Falls back to the server's IMAP <c>INTERNALDATE</c> — when the message was delivered,
    ///     which is what the accounting import wants anyway and is also the value the AB#5340
    ///     <c>SINCE</c> window is evaluated against, so a mail inside the configured period cannot
    ///     get a date outside it. Returns <c>null</c> when neither is available: "unknown" is a
    ///     state the consumer can handle, year 1 is not.
    ///     </para>
    ///     <para>
    ///     Both sources are read with <see cref="DateTimeOffset.DateTime" /> — the wall-clock time
    ///     as stated, without the offset — because that is what the header path has always emitted;
    ///     converting to UTC here would shift every existing timestamp.
    ///     </para>
    /// </summary>
    internal static DateTime? ResolveReceivedAt(DateTimeOffset headerDate, DateTimeOffset? internalDate)
    {
        if (IsUsableDate(headerDate))
        {
            return headerDate.DateTime;
        }

        if (internalDate.HasValue && IsUsableDate(internalDate.Value))
        {
            return internalDate.Value.DateTime;
        }

        return null;
    }

    /// <summary>
    ///     A date is usable unless it is the year-1 sentinel every "no value" path produces —
    ///     MimeKit's unset <c>Date:</c>, an unparsable one, and a default-valued INTERNALDATE alike.
    /// </summary>
    private static bool IsUsableDate(DateTimeOffset value)
    {
        return value.Year > 1;
    }

    /// <summary>
    ///     What one finished polling pass may write back to the IMAP server (AB#5337).
    /// </summary>
    /// <param name="Flags">The flags to add to the batch's messages; <see cref="MessageFlags.None" /> means no write.</param>
    /// <param name="Expunge">Whether the folder must be expunged afterwards, which is what actually removes them.</param>
    internal readonly record struct EmailFlagDecision(MessageFlags Flags, bool Expunge)
    {
        internal bool HasFlags => Flags != MessageFlags.None;
    }

    /// <summary>
    ///     Decides the server-side FLAG write-back for a finished poll (AB#5337, AB#5345). The
    ///     decision is taken AFTER the pipeline ran and only when
    ///     <see cref="MailSuccessPath.IsPostProcessingAllowed" /> let it through; otherwise nothing
    ///     is written at all, so the mail stays exactly as the server has it and is still there to
    ///     be imported.
    ///     <para>
    ///     This used to happen per message inside the fetch loop, before the pipeline was even
    ///     invoked. Under the default <c>onlyUnread: true</c> the next poll asks the server for
    ///     <c>NOT SEEN</c>, so a mail flagged ahead of a failing run was never offered again — a
    ///     receipt lost on a bookkeeping input channel, with nothing missing that anyone would
    ///     notice (prod-1/gastroacker, 2026-09-23).
    ///     </para>
    ///     <para>
    ///     Covers the two FLAG modes only (<see cref="MailPostProcessingMode.Delete" /> and
    ///     <see cref="MailPostProcessingMode.MarkAsRead" />);
    ///     <see cref="MailPostProcessingMode.MoveToFolders" /> writes no flags and is carried out by
    ///     <see cref="ApplyPostProcessingAsync" />.
    ///     </para>
    /// </summary>
    /// <remarks>
    ///     🔴 The legacy branch is load-bearing, not tidiness: a pipeline that names NO
    ///     <c>postProcessingMode</c> keeps reading its two flags INDEPENDENTLY, so the combination
    ///     "mark read AND delete" still produces one write carrying both. Collapsing that onto the
    ///     derived single mode would be invisible in practice (an expunged message's <c>\Seen</c>
    ///     flag is observable by nobody) but it would change what reaches the server on a fleet of
    ///     deployed pipelines, and this method's whole job is that nothing about them changes.
    /// </remarks>
    internal static EmailFlagDecision ResolveFlagDecision(FromEmailNodeConfiguration nodeConfig,
        bool writeBackAllowed)
    {
        if (!writeBackAllowed)
        {
            return new EmailFlagDecision(MessageFlags.None, false);
        }

        var mode = ResolveEffectivePostProcessingMode(nodeConfig);

        if (nodeConfig.PostProcessingMode is null && mode is MailPostProcessingMode.Delete
                or MailPostProcessingMode.MarkAsRead)
        {
            var legacyFlags = MessageFlags.None;
            if (nodeConfig.MarkAsRead)
            {
                legacyFlags |= MessageFlags.Seen;
            }

            if (nodeConfig.DeleteAfterProcessing)
            {
                legacyFlags |= MessageFlags.Deleted;
            }

            // `\Deleted` alone only marks; the messages disappear when the folder is expunged.
            return new EmailFlagDecision(legacyFlags, nodeConfig.DeleteAfterProcessing);
        }

        return mode switch
        {
            MailPostProcessingMode.Delete => new EmailFlagDecision(MessageFlags.Deleted, true),
            MailPostProcessingMode.MarkAsRead => new EmailFlagDecision(MessageFlags.Seen, false),
            _ => new EmailFlagDecision(MessageFlags.None, false),
        };
    }

    /// <summary>
    ///     The post-processing mode actually in force (AB#5345): the configured one, or — when none
    ///     is configured — the one DERIVED from what this pipeline already said.
    /// </summary>
    /// <remarks>
    ///     The derivation is the migration, and it is deliberately total: every combination of the
    ///     pre-AB#5345 properties maps onto a mode that behaves as that combination did.
    ///     A done/failed folder (both new, so only a pipeline that opted in has one) means the
    ///     three-folder mode; otherwise <c>deleteAfterProcessing</c> wins over <c>markAsRead</c>,
    ///     because a message that is deleted and expunged cannot meaningfully also be "read"; with
    ///     neither flag the node leaves the mailbox alone, which is a state operators rely on when
    ///     they poll a shared folder.
    /// </remarks>
    internal static MailPostProcessingMode ResolveEffectivePostProcessingMode(
        FromEmailNodeConfiguration nodeConfig)
    {
        if (nodeConfig.PostProcessingMode.HasValue)
        {
            return nodeConfig.PostProcessingMode.Value;
        }

        if (!string.IsNullOrWhiteSpace(nodeConfig.DoneFolder) ||
            !string.IsNullOrWhiteSpace(nodeConfig.FailedFolder))
        {
            return MailPostProcessingMode.MoveToFolders;
        }

        if (nodeConfig.DeleteAfterProcessing)
        {
            return MailPostProcessingMode.Delete;
        }

        return nodeConfig.MarkAsRead
            ? MailPostProcessingMode.MarkAsRead
            : MailPostProcessingMode.None;
    }

    /// <summary>
    ///     The folder this trigger polls: the settings/definition <c>sourceFolder</c> when one is
    ///     given, otherwise the connection entity's <c>Folder</c> — which is where it has always
    ///     lived and where a plain single-folder import still configures it.
    /// </summary>
    internal static string ResolveSourceFolderName(FromEmailNodeConfiguration nodeConfig,
        string connectionFolder)
    {
        return string.IsNullOrWhiteSpace(nodeConfig.SourceFolder) ? connectionFolder : nodeConfig.SourceFolder;
    }

    private static string ResolveSourceFolderName(FromEmailNodeConfiguration nodeConfig,
        EmailServerConfiguration serverConfig)
    {
        return ResolveSourceFolderName(nodeConfig, serverConfig.Folder);
    }

    /// <summary>
    ///     Carries out the mode's post-processing for one finished poll (AB#5345).
    ///     <paramref name="importConfirmed" /> is the BUSINESS outcome — an inbox item was created —
    ///     as the pipeline reported it through <c>successPath</c>, never "the execution did not
    ///     throw"; a run that threw never reaches here at all.
    /// </summary>
    private async Task ApplyPostProcessingAsync(IMailFolder folder, IList<UniqueId> uids,
        FromEmailNodeConfiguration nodeConfig, bool importConfirmed)
    {
        if (ResolveEffectivePostProcessingMode(nodeConfig) == MailPostProcessingMode.MoveToFolders)
        {
            await MoveBatchAsync(folder, uids, nodeConfig, importConfirmed);
            return;
        }

        var flagDecision = ResolveFlagDecision(nodeConfig, importConfirmed);
        if (flagDecision.HasFlags)
        {
            await folder.AddFlagsAsync(uids, flagDecision.Flags, true);
        }

        if (flagDecision.Expunge)
        {
            await folder.ExpungeAsync();
        }
    }

    /// <summary>
    ///     Three-folder mode: a confirmed batch goes to the done folder, an unconfirmed one to the
    ///     failed folder. A target that is not configured or cannot be resolved leaves the batch
    ///     where it is and is logged — never fails the poll: the folder path is operator-entered
    ///     text, and a typo in it must cost the move, not the import.
    /// </summary>
    private async Task MoveBatchAsync(IMailFolder folder, IList<UniqueId> uids,
        FromEmailNodeConfiguration nodeConfig, bool importConfirmed)
    {
        var targetPath = importConfirmed ? nodeConfig.DoneFolder : nodeConfig.FailedFolder;
        if (string.IsNullOrWhiteSpace(targetPath))
        {
            logger.LogWarning(
                "FromEmail: postProcessingMode is MoveToFolders but no {Which} folder is configured; " +
                "{Count} mail(s) stay in the source folder.",
                importConfirmed ? "done" : "failed", uids.Count);
            return;
        }

        IMailFolder? target;
        try
        {
            target = await ResolveFolderAsync(targetPath, createLeafIfMissing: true);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            logger.LogError(ex,
                "FromEmail: could not resolve the folder '{Folder}'; {Count} mail(s) stay in the source folder.",
                targetPath, uids.Count);
            return;
        }

        if (target == null)
        {
            logger.LogError(
                "FromEmail: the folder '{Folder}' does not exist and its parent path could not be walked; " +
                "{Count} mail(s) stay in the source folder.",
                targetPath, uids.Count);
            return;
        }

        await folder.MoveToAsync(uids, target);
        logger.LogInformation("FromEmail: moved {Count} mail(s) to '{Folder}' ({Outcome}).",
            uids.Count, targetPath, importConfirmed ? "import confirmed" : "import NOT confirmed");
    }

    /// <summary>
    ///     Resolves a <c>/</c>-separated folder path against the mailbox, creating the LEAF when
    ///     <paramref name="createLeafIfMissing" /> is set (its parents must exist). Returns
    ///     <c>null</c> when the path cannot be walked.
    /// </summary>
    /// <remarks>
    ///     The verbatim lookup is tried FIRST, because that is how this node has always addressed
    ///     the polled folder and a name that already carries the server's own delimiter must keep
    ///     working. Only when that fails is the path split on <c>/</c> and walked segment by
    ///     segment from the personal namespace — IMAP servers do not agree on a hierarchy
    ///     delimiter (Dovecot commonly uses <c>.</c>, others <c>/</c>), so an operator writing
    ///     <c>Archive/Invoices/Done</c> must not have to know which one their server picked.
    ///     A folder whose NAME contains a <c>/</c> is consequently not addressable by path.
    /// </remarks>
    private async Task<IMailFolder?> ResolveFolderAsync(string path, bool createLeafIfMissing)
    {
        try
        {
            return await _imapClient!.GetFolderAsync(path);
        }
        catch (FolderNotFoundException)
        {
            // Not there under that literal name — walk it instead.
        }

        var segments = SplitFolderPath(path);
        if (segments.Count == 0 || _imapClient!.PersonalNamespaces.Count == 0)
        {
            return null;
        }

        var current = _imapClient.GetFolder(_imapClient.PersonalNamespaces[0]);
        for (var i = 0; i < segments.Count; i++)
        {
            IMailFolder? next;
            try
            {
                next = await current.GetSubfolderAsync(segments[i]);
            }
            catch (FolderNotFoundException)
            {
                next = null;
            }

            if (next == null)
            {
                // Only the LEAF may be created — a missing parent is a path the operator has to
                // fix, and creating one silently would invent a folder tree nobody asked for.
                if (i != segments.Count - 1 || !createLeafIfMissing)
                {
                    return null;
                }

                next = await current.CreateAsync(segments[i], true);
            }

            // A server that answers a create/lookup with nothing is a path we cannot walk any
            // further; treated as "unresolvable" rather than dereferenced.
            if (next == null)
            {
                return null;
            }

            current = next;
        }

        return current;
    }

    /// <summary>
    ///     Splits an operator-entered folder path into its segments. <c>/</c> is the ONE separator
    ///     this node understands on input; the server's own delimiter is applied by MailKit when the
    ///     walk asks for a subfolder.
    /// </summary>
    internal static IReadOnlyList<string> SplitFolderPath(string? path)
    {
        return string.IsNullOrWhiteSpace(path)
            ? []
            : path.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    /// <summary>
    ///     Surfaces the mail's <c>Authentication-Results</c> (DKIM/DMARC) verdict onto
    ///     <see cref="EmailData.Authentication" /> and <see cref="EmailData.Headers" /> (AB#5125).
    ///     Only the FIRST occurrence is trusted — the receiving server PREPENDS its own header rather
    ///     than replacing a sender-supplied one, so a later occurrence is sender-controlled text — and
    ///     the count is passed to the parser so a second header defeats <c>IsDmarcPass</c>. Absent
    ///     header ⇒ <see cref="EmailData.Authentication" /> stays null ("nothing known", NOT "failed"),
    ///     which the trust mapper treats fail-safe as Weak.
    /// </summary>
    internal static void PopulateAuthentication(EmailData emailData, MimeMessage message)
    {
        string? firstValue = null;
        var count = 0;
        foreach (var header in message.Headers)
        {
            if (!string.Equals(header.Field, AuthenticationResultsParser.HeaderName,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            count++;
            firstValue ??= header.Value;
        }

        if (count == 0)
        {
            return;
        }

        // TryAdd: first occurrence wins, matching the Graph trigger's EmailData.Headers contract.
        emailData.Headers.TryAdd(AuthenticationResultsParser.HeaderName, firstValue ?? string.Empty);
        emailData.Authentication = AuthenticationResultsParser.Parse(firstValue, count);
    }
}

/// <summary>
/// Represents an email message
/// </summary>
public class EmailData
{
    /// <summary>
    /// Email subject
    /// </summary>
    public string? Subject { get; set; }
    
    /// <summary>
    /// Email sender (display name and address)
    /// </summary>
    public string? From { get; set; }

    /// <summary>
    /// Email sender address only (e.g. user@example.com)
    /// </summary>
    public string? FromAddress { get; set; }
    
    /// <summary>
    /// Email recipients
    /// </summary>
    public string? To { get; set; }
    
    /// <summary>
    /// When the message was received. <b>Nullable</b> since AB#5339: a mail may carry no usable
    /// <c>Date:</c> header at all, and the IMAP trigger falls back to the server's INTERNALDATE
    /// before giving up.
    /// </summary>
    /// <remarks>
    /// 🔴 A null here means <b>unknown</b> and must stay distinguishable from a date. The
    /// non-nullable predecessor made "no date" indistinguishable from
    /// <c>0001-01-01T00:00:00</c> — written through to <c>UploadedDocument.SourceReceivedAt</c> it
    /// sorts to the very front of the accounting inbox and falls outside every fiscal year, so the
    /// receipt cannot be assigned to a period. Consumers read this via JSONPath (<c>$.key.Date</c>)
    /// and land on <c>CreateUpdateInfo@1</c>, which writes a JSON null as a null attribute value
    /// and skips a path that matches nothing — both are "unknown", which is the truth.
    /// </remarks>
    public DateTime? Date { get; set; }
    
    /// <summary>
    /// Email body (text or HTML)
    /// </summary>
    public string? Body { get; set; }
    
    /// <summary>
    /// HTML body of the email
    /// </summary>
    public string? HtmlBody { get; set; }
    
    /// <summary>
    /// Plain text body of the email
    /// </summary>
    public string? TextBody { get; set; }
    
    /// <summary>
    /// Email message ID
    /// </summary>
    public string? MessageId { get; set; }
    
    /// <summary>
    /// List of email attachments
    /// </summary>
    public List<AttachmentData> Attachments { get; set; } = new();

    /// <summary>
    /// Selected internet message headers of the mail, keyed by header name (case insensitive).
    /// Empty unless the trigger was configured to fetch them. AB#5011.
    /// </summary>
    /// <remarks>
    /// 🔴 Only the <b>first</b> occurrence of a name is kept. A header may legitimately appear
    /// several times, but for the trust-bearing ones only the topmost was written by the receiving
    /// infrastructure — a sender can put their own copy into the message they submit, and the server
    /// prepends rather than replaces. Joining the occurrences would let a forged
    /// <c>Authentication-Results: …; dmarc=pass</c> be found by any downstream substring or regex
    /// check. <see cref="Authentication"/> reports how many there were.
    /// <para>
    /// Populated by <c>FromMicrosoftGraphEmail@1</c> (its configured header set) and by the IMAP
    /// trigger (<c>FromEmail@1</c>), which surfaces the <c>Authentication-Results</c> header (AB#5125).
    /// </para>
    /// </remarks>
    public Dictionary<string, string> Headers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// SPF / DKIM / DMARC verdicts parsed out of the mail's <c>Authentication-Results</c> header —
    /// the only evidence a pipeline has that the claimed sender really sent the mail. Null when the
    /// trigger was not configured to fetch headers, or when the mail carried no such header
    /// (an internally generated or relayed mail may not). AB#5011.
    /// </summary>
    /// <remarks>
    /// A null here is <b>not</b> "authentication failed" and must not be treated as one — it means
    /// nothing is known. A gate has to decide explicitly what to do with an unknown verdict, and
    /// which way that falls is a tenant policy question, not something the trigger may decide.
    /// </remarks>
    public EmailAuthenticationResults? Authentication { get; set; }

    /// <summary>
    /// True when at least one attachment is a PDF. Lets a pipeline decide whether
    /// the mail carries a document to stage or should be kept as a receipt itself
    /// (render the mail body). Inline images/logos are not PDFs and do not count.
    /// </summary>
    // AB#4433: a PDF may arrive with a generic contentType (application/octet-stream);
    // also treat a ".pdf" file name as a PDF so a mislabeled invoice is not dropped
    // in favor of rendering the mail body.
    public bool HasPdfAttachment =>
        Attachments.Any(a =>
            string.Equals(a.ContentType, "application/pdf", StringComparison.OrdinalIgnoreCase) ||
            a.FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// True when the mail carries at least one attachment that looks like a
    /// photographed/scanned receipt embedded in the message (see
    /// <see cref="AttachmentData.IsLikelyReceiptImage"/>). Lets the accounting
    /// email import convert such an image to a PDF instead of rendering the mail
    /// body. AB#4647.
    /// </summary>
    public bool HasReceiptImageAttachment => Attachments.Any(a => a.IsLikelyReceiptImage);

    /// <summary>
    /// True when the mail carries at least one attachment worth staging as its own
    /// document (a PDF or a receipt-like image). When false, the accounting email
    /// import renders the mail body as the receipt instead. AB#4647.
    /// </summary>
    public bool HasStageableAttachment => Attachments.Any(a => a.IsLikelyDocument);
}

/// <summary>
/// Represents an email attachment
/// </summary>
public class AttachmentData
{
    /// <summary>
    /// Attachment file name
    /// </summary>
    public string FileName { get; set; } = string.Empty;

    /// <summary>
    /// MIME content type
    /// </summary>
    public string ContentType { get; set; } = string.Empty;

    /// <summary>
    /// Base64-encoded attachment content
    /// </summary>
    public string? Data { get; set; }

    /// <summary>
    /// Content length in bytes
    /// </summary>
    public long Length { get; set; }

    /// <summary>
    /// True when the attachment is embedded inline in the message body
    /// (referenced via a cid: content-id, e.g. a photo pasted into the mail),
    /// rather than a regular file attachment. AB#4647.
    /// </summary>
    public bool IsInline { get; set; }

    // AB#4647: camera / scanner / messenger / screenshot file-name patterns. Used to
    // tell a photographed-or-scanned receipt apart from a signature logo or office
    // artwork, which cannot be done by size (a 29 KB receipt photo is smaller than
    // many logos). Deliberately precision-first: a randomly named pasted image is not
    // matched and falls through to body rendering rather than adding inbox noise.
    private static readonly Regex CameraFileNameRegex = new(
        @"(?:^|[^a-z0-9])(?:img|pxl|dsc|dscf|dcim|dji|gopr|scan|scanned|photo|foto|signal|whatsapp|screenshot|bildschirmfoto)(?:[-_ ]?\d|\b)|\.(?:heic|heif)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private const long MinReceiptImageBytes = 5_000;

    // AB#4647: a non-trivial inline JPEG/HEIC without a camera file name is still very
    // likely a photographed receipt — mail clients (Outlook/Apple Mail) frequently rename
    // a pasted photo to a generic "imageN.jpeg". Signature logos are overwhelmingly PNG/GIF
    // (or tiny), so a sizable inline *photo* content type is a strong receipt signal even
    // without a camera name. Byte size and logo size overlap, so the occasional large JPEG
    // signature logo is accepted as a false positive — SHA-256 dedup means an identical
    // recurring logo is staged at most once.
    private const long InlinePhotoMinBytes = 30_000;

    private static bool IsPhotoContentType(string contentType) =>
        contentType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/jpg", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/pjpeg", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/heic", StringComparison.OrdinalIgnoreCase) ||
        contentType.Equals("image/heif", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Heuristic: true when this image attachment should be treated as a receipt/document.
    /// An image content type (excluding animated/decorative GIF) above a small floor, and
    /// either a regular (deliberately attached) file — where the user's intent to send a
    /// document is explicit — or, when embedded <see cref="IsInline"/> in the body, one that
    /// either matches a camera/scanner/messenger/screenshot file name OR is a sizable JPEG/HEIC
    /// photo (a pasted receipt the mail client renamed to e.g. "image0.jpeg"). Signature logos,
    /// which only the Microsoft Graph channel surfaces inline, are typically PNG/GIF or tiny and
    /// so are excluded. AB#4647.
    /// </summary>
    public bool IsLikelyReceiptImage =>
        ContentType.StartsWith("image/", StringComparison.OrdinalIgnoreCase) &&
        !ContentType.Equals("image/gif", StringComparison.OrdinalIgnoreCase) &&
        Length >= MinReceiptImageBytes &&
        (!IsInline
         || CameraFileNameRegex.IsMatch(FileName)
         || (IsPhotoContentType(ContentType) && Length >= InlinePhotoMinBytes));

    /// <summary>
    /// True when this attachment should be staged as its own accounting document: a
    /// PDF, or an image that looks like a receipt (<see cref="IsLikelyReceiptImage"/>).
    /// Signature logos and icons are excluded. AB#4647.
    /// </summary>
    public bool IsLikelyDocument =>
        ContentType.Equals("application/pdf", StringComparison.OrdinalIgnoreCase) ||
        FileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) ||
        IsLikelyReceiptImage;
}

/// <summary>
/// Represents a batch of emails
/// </summary>
public class EmailBatch
{
    /// <summary>
    /// List of emails in the batch
    /// </summary>
    public List<EmailData> Emails { get; set; } = new();
    
    /// <summary>
    /// Number of emails in the batch
    /// </summary>
    public int Count { get; set; }
    
    /// <summary>
    /// Timestamp when the batch was processed
    /// </summary>
    public DateTime ProcessedAt { get; set; }
}