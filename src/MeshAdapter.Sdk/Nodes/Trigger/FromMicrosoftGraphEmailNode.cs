using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using MimeKit;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromMicrosoftGraphEmailNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromMicrosoftGraphEmailNode(
    ILogger<FromMicrosoftGraphEmailNode> logger,
    IHttpClientFactory httpClientFactory,
    IChannelCallerBinder callerBinder)
    : ITriggerPipelineNode
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";

    /// <summary>The node as a pipeline definition names it — used in configuration errors.</summary>
    private const string NodeType = "FromMicrosoftGraphEmail@1";

    /// <summary>
    ///     AB#5372: what an operator of THIS channel does to get a mode, appended to the
    ///     "no mode configured" error. This channel derives only MoveToFolders, and only from a
    ///     configured done or failure folder — there are no legacy flags here.
    /// </summary>
    private const string HowToFix =
        "The legacy derivation is still honoured, so configuring moveToFolderPathOnSuccess or " +
        "moveToFolderPathOnFailure yields MoveToFolders — which is also the only mode that can park " +
        "a message whose attempts are exhausted.";

    /// <summary>AB#5372: the repair hint for a settings entity that still stores <c>None</c>.</summary>
    private const string RemovedModeSuggestion =
        "MoveToFolders is what this channel has always done and the only mode with somewhere to park " +
        "a message whose attempts are exhausted; configure the done folder alongside it.";

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;

    /// <summary>
    ///     AB#5345: the "no successPath configured, so nothing is verified" warning is worth saying
    ///     once per process rather than once per message.
    /// </summary>
    private bool _successPathWarningLogged;

    // ReSharper disable once ClassNeverInstantiated.Local
    private record GraphConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string AzureTenantId { get; init; }
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    public Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromMicrosoftGraphEmailNodeConfiguration>();

        if (!context.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext,
                nameof(c.ServerConfiguration),
                c.ServerConfiguration);
        }

        var graphConfig = context.GlobalConfiguration.GetValue<GraphConfiguration>(c.ServerConfiguration);

        // Mailbox / folders may live in a configuration entity instead of the pipeline
        // definition (see SettingsConfiguration) so a redeploy never overwrites what an
        // operator configured and nothing tenant-specific leaks into the seed. A value
        // found in the settings configuration takes precedence over the node property.
        var effectiveConfig = ResolveEffectiveConfiguration(context.GlobalConfiguration, c);

        // AB#5372: the post-processing mode is resolved HERE, unconditionally and before the poll
        // loop exists — a configuration that yields no mode throws out of this call. The three valid
        // modes are the mailbox's bookkeeping (see MailPostProcessingMode): a trigger that changes
        // nothing hands the next poll the same messages for ever and imports nothing new.
        var postProcessingMode = ResolveEffectivePostProcessingMode(effectiveConfig);

        if (!string.IsNullOrWhiteSpace(c.SettingsConfiguration))
        {
            logger.LogInformation(
                "FromMicrosoftGraphEmail: resolved mailbox/folders from settings configuration '{Settings}' (folder='{Folder}', moveTo='{MoveTo}', postProcessing={Mode})",
                c.SettingsConfiguration, effectiveConfig.FolderPath, effectiveConfig.MoveToFolderPathOnSuccess,
                postProcessingMode);
        }

        if (string.IsNullOrWhiteSpace(effectiveConfig.Mailbox))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext, nameof(c.Mailbox),
                c.SettingsConfiguration ?? c.ServerConfiguration);
        }

        if (string.IsNullOrWhiteSpace(effectiveConfig.FolderPath))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext, nameof(c.FolderPath),
                c.SettingsConfiguration ?? c.ServerConfiguration);
        }

        // AB#5345: a confirmation path that cannot name exactly one value is a configuration
        // mistake, and it has to be heard when the pipeline is deployed rather than be read as
        // "never confirmed" on every poll for the rest of the adapter's life.
        if (!string.IsNullOrWhiteSpace(effectiveConfig.SuccessPath) &&
            !MailSuccessPath.TryParse(effectiveConfig.SuccessPath, out _))
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(
                context.NodeContext, effectiveConfig.SuccessPath);
        }

        _cancellationTokenSource = new CancellationTokenSource();
        // The DEFINITION config is handed over, not the resolved one: the settings are resolved
        // again on every poll so a changed setting is read where it is used (AB#5345).
        _pollingTask = Task.Run(
            async () => await PollForMessagesAsync(context, graphConfig, c),
            _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a copy of <paramref name="c"/> with every RUNTIME setting — mailbox, the three
    /// folder paths, poll interval, post-processing mode, batch cap, sender filter and the import
    /// confirmation path — resolved from the optional settings configuration (well-known name
    /// <see cref="FromMicrosoftGraphEmailNodeConfiguration.SettingsConfiguration"/>).
    /// A non-empty settings value overrides the corresponding node property; anything
    /// missing falls back to the node property. The node stays domain-agnostic: which
    /// attributes to read is given by the *Attribute node properties.
    /// <para>
    /// 🔴 The rule this serves (AB#5345): <b>setting a setting must never rewrite a pipeline
    /// definition.</b> The definition is release content; a poll interval or a folder path is
    /// operating state. The readers themselves are shared with <c>FromEmail@1</c>
    /// (<see cref="ConfigurationSettingsReader"/>), so the two mail channels resolve their
    /// settings by one set of rules.
    /// </para>
    /// </summary>
    internal static FromMicrosoftGraphEmailNodeConfiguration ResolveEffectiveConfiguration(
        IGlobalConfiguration globalConfiguration, FromMicrosoftGraphEmailNodeConfiguration c)
    {
        var attributes = ConfigurationSettingsReader.TryGetAttributes(
            globalConfiguration, c.SettingsConfiguration);
        if (attributes is null)
        {
            // No settings configuration, undefined, or a malformed payload — keep the
            // node properties (validated by the caller).
            return c;
        }

        var attrs = attributes.Value;
        return c with
        {
            Mailbox = ConfigurationSettingsReader.ReadString(attrs, c.MailboxAttribute) ?? c.Mailbox,
            FolderPath = ConfigurationSettingsReader.ReadString(attrs, c.SourceFolderAttribute) ?? c.FolderPath,
            MoveToFolderPathOnSuccess =
                ConfigurationSettingsReader.ReadString(attrs, c.DoneFolderAttribute) ?? c.MoveToFolderPathOnSuccess,
            MoveToFolderPathOnFailure =
                ConfigurationSettingsReader.ReadString(attrs, c.FailedFolderAttribute) ?? c.MoveToFolderPathOnFailure,
            PollingIntervalSeconds =
                ConfigurationSettingsReader.ReadPositiveInt(attrs, c.PollingSecondsAttribute) ?? c.PollingIntervalSeconds,
            // AB#5345: the remaining runtime settings, same rule — settings win, node property is
            // the fallback, and an unset attribute changes nothing about a deployed pipeline.
            // AB#5372: read through MailPostProcessingModeSetting, not ReadEnum directly — a stored
            // `None` must not degrade to "not configured" and be replaced by a derived mode.
            PostProcessingMode =
                MailPostProcessingModeSetting.Read(attrs, c.PostProcessingModeAttribute,
                    NodeType, RemovedModeSuggestion) ?? c.PostProcessingMode,
            MaxMessagesPerPoll =
                ConfigurationSettingsReader.ReadPositiveInt(attrs, c.MaxMessagesPerPollAttribute)
                ?? c.MaxMessagesPerPoll,
            SenderFilter =
                ConfigurationSettingsReader.ReadString(attrs, c.SenderFilterAttribute) ?? c.SenderFilter,
            SuccessPath =
                ConfigurationSettingsReader.ReadString(attrs, c.SuccessPathAttribute) ?? c.SuccessPath,
        };
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
                logger.LogWarning("Graph email polling task did not complete within timeout");
            }
            catch (OperationCanceledException)
            {
                // Expected: the polling task observed the cancellation we just requested.
            }
            catch (Exception ex)
            {
                // Teardown must never rethrow — a faulted polling task must not fail the
                // trigger unregistration / config reconcile (AB#4761).
                logger.LogWarning(ex, "Graph email polling task faulted during stop");
            }
        }

        _cancellationTokenSource?.Dispose();
    }

    private async Task PollForMessagesAsync(ITriggerContext context, GraphConfiguration graphConfig,
        FromMicrosoftGraphEmailNodeConfiguration definitionConfig)
    {
        string? sourceFolderId = null;
        string? targetFolderId = null;
        string? failureFolderId = null;
        // AB#5345: the paths the cached folder ids above were resolved FROM. The settings are
        // re-read on every poll, so an id has to be dropped the moment the path behind it changes —
        // otherwise a corrected folder would keep filing mail into the old one.
        string? resolvedSourcePath = null;
        string? resolvedDonePath = null;
        string? resolvedFailedPath = null;
        // The mailbox the last poll actually used, for the poll-level error message: the mailbox
        // may come from the settings entity, in which case the definition carries none at all.
        var lastKnownMailbox = definitionConfig.Mailbox;
        // AB#5260: a failure folder that cannot be resolved must not take the import
        // down. Once the path turns out to be unusable we degrade to "skip exhausted
        // messages" (the pre-AB#5142 behaviour) instead of failing every poll — the
        // path is operator-entered configuration, and a typo in it used to stop
        // *every* message from being imported, not just the exhausted ones.
        var failureFolderUnusable = false;
        // AB#5260: the markers are only visible in Outlook/OWA once their names exist in
        // the mailbox's master category list. Ensured once per node lifetime; see
        // TryRegisterImportCategoriesAsync for what "once" means when it fails.
        var categoriesEnsured = false;
        // In-process fallback attempt counter for mailboxes where the category stamp
        // cannot be written; the authoritative count lives ON the message as an
        // Outlook category (AB#5142), so it survives adapter restarts and counts
        // runs that killed the process (e.g. an OOM in a render node) and therefore
        // never reported a failure.
        var failureCounts = new Dictionary<string, AttemptMemory>();
        // Avoids re-warning every poll about an exhausted message that stays in the
        // source folder because no failure folder is configured.
        var exhaustedLogged = new HashSet<string>();

        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                // AB#5345: resolved HERE, once per poll, not once in StartAsync — a setting is
                // operating state and the node must read it where it uses it.
                var nodeConfig = ResolveEffectiveConfiguration(context.GlobalConfiguration, definitionConfig);
                var postProcessingMode = ResolveEffectivePostProcessingMode(nodeConfig);
                lastKnownMailbox = nodeConfig.Mailbox;

                if (!string.Equals(resolvedSourcePath, nodeConfig.FolderPath, StringComparison.Ordinal))
                {
                    resolvedSourcePath = nodeConfig.FolderPath;
                    sourceFolderId = null;
                }

                if (!string.Equals(resolvedDonePath, nodeConfig.MoveToFolderPathOnSuccess,
                        StringComparison.Ordinal))
                {
                    resolvedDonePath = nodeConfig.MoveToFolderPathOnSuccess;
                    targetFolderId = null;
                }

                if (!string.Equals(resolvedFailedPath, nodeConfig.MoveToFolderPathOnFailure,
                        StringComparison.Ordinal))
                {
                    resolvedFailedPath = nodeConfig.MoveToFolderPathOnFailure;
                    failureFolderId = null;
                    // A corrected path deserves a fresh attempt at resolving it.
                    failureFolderUnusable = false;
                }

                var accessToken = await GetAccessTokenAsync(graphConfig);

                sourceFolderId ??= await ResolveFolderIdAsync(accessToken, nodeConfig.Mailbox,
                    nodeConfig.FolderPath, createLeafIfMissing: false);
                if (postProcessingMode == MailPostProcessingMode.MoveToFolders &&
                    !string.IsNullOrWhiteSpace(nodeConfig.MoveToFolderPathOnSuccess))
                {
                    targetFolderId ??= await ResolveFolderIdAsync(accessToken, nodeConfig.Mailbox,
                        nodeConfig.MoveToFolderPathOnSuccess, createLeafIfMissing: true);
                }

                if (postProcessingMode == MailPostProcessingMode.MoveToFolders &&
                    !string.IsNullOrWhiteSpace(nodeConfig.MoveToFolderPathOnFailure) && !failureFolderUnusable)
                {
                    // AB#5260: resolved inside its own try — the failure folder is a
                    // convenience for messages that already failed, so a bad path must
                    // degrade this one feature and never the import as a whole.
                    try
                    {
                        failureFolderId ??= await ResolveFolderIdAsync(accessToken, nodeConfig.Mailbox,
                            nodeConfig.MoveToFolderPathOnFailure, createLeafIfMissing: true);
                    }
                    catch (InvalidOperationException ex)
                    {
                        // Only a *path* problem is treated as permanent — that is the one
                        // ResolveFolderIdAsync reports as InvalidOperationException and the
                        // one no amount of retrying fixes. Connectivity errors keep
                        // bubbling to the poll-level handler, which backs off and retries.
                        failureFolderUnusable = true;
                        logger.LogError(ex,
                            "Could not resolve the failure folder '{Folder}' in mailbox '{Mailbox}'; " +
                            "exhausted messages stay in '{SourceFolder}' and are skipped until the path is corrected",
                            nodeConfig.MoveToFolderPathOnFailure, nodeConfig.Mailbox, nodeConfig.FolderPath);
                    }
                }

                if (!categoriesEnsured)
                {
                    categoriesEnsured = await TryRegisterImportCategoriesAsync(accessToken, nodeConfig.Mailbox,
                        nodeConfig.MaxAttemptsPerMessage);
                }

                var messages = await GetMessagesAsync(accessToken, nodeConfig, sourceFolderId);

                foreach (var message in messages)
                {
                    if (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        break;
                    }

                    var messageId = message.GetProperty("id").GetString();
                    if (messageId == null)
                    {
                        continue;
                    }

                    var subject = message.TryGetProperty("subject", out var subj) ? subj.GetString() : null;
                    var categories = GetCategories(message);

                    // AB#5260 — the retry affordance. A message carrying the parked marker can
                    // only be in the SOURCE folder because a human put it back: the adapter
                    // stamps that marker exclusively on messages it moves into the failure
                    // folder in the same breath, and never moves one back. So the move itself
                    // is the reset gesture ("drag it back in and it gets picked up again") —
                    // no Graph access, no category surgery, no adapter restart.
                    var parked = HasFailedCategory(categories);
                    if (parked)
                    {
                        var cleared = WithoutImportMarkers(categories);
                        if (await TrySetCategoriesAsync(accessToken, nodeConfig.Mailbox, messageId, cleared))
                        {
                            categories = cleared;
                            parked = false;
                            failureCounts.Remove(messageId);
                            exhaustedLogged.Remove(messageId);
                            logger.LogInformation(
                                "Mail '{Subject}' was moved back into '{Folder}'; import markers cleared, " +
                                "retrying it with a full attempt budget",
                                subject, nodeConfig.FolderPath);
                        }
                        // Else: the marker could not be cleared (TrySetCategoriesAsync logged
                        // why). Importing anyway would loop forever — the marker would still be
                        // there on the next poll and read as another reset — so the message stays
                        // parked and is moved aside again below.
                    }

                    var attempts = ResolveAttemptCount(categories,
                        failureCounts.TryGetValue(messageId, out var remembered) ? remembered : null,
                        out var operatorReset);
                    if (operatorReset)
                    {
                        failureCounts.Remove(messageId);
                        logger.LogInformation(
                            "The attempt marker on mail '{Subject}' was cleared in the mailbox; " +
                            "forgetting the in-process attempt count and retrying it",
                            subject);
                    }

                    if (parked || attempts >= nodeConfig.MaxAttemptsPerMessage)
                    {
                        if (failureFolderId != null)
                        {
                            var parkedId = await MoveMessageAsync(accessToken, nodeConfig.Mailbox, messageId,
                                failureFolderId);
                            // Marked AFTER the move, on the id the move minted (Graph gives the
                            // moved copy a new one): a stamp written before a move that then fails
                            // would leave the parked marker on a message still sitting in the
                            // source folder, where the next poll would read it as an operator
                            // reset and hand a poison message a fresh attempt budget (AB#5260).
                            await TrySetCategoriesAsync(accessToken, nodeConfig.Mailbox, parkedId,
                                WithFailedCategory(categories));
                            failureCounts.Remove(messageId);
                            exhaustedLogged.Remove(messageId);
                            logger.LogWarning(
                                "Mail '{Subject}' failed {MaxAttempts} attempt(s); moved to '{Folder}' and " +
                                "marked '{Marker}' — move it back into '{SourceFolder}' to have it imported again",
                                subject, nodeConfig.MaxAttemptsPerMessage, nodeConfig.MoveToFolderPathOnFailure,
                                FailedCategory, nodeConfig.FolderPath);
                        }
                        else if (exhaustedLogged.Add(messageId))
                        {
                            logger.LogWarning(
                                "Mail '{Subject}' failed {MaxAttempts} attempt(s); skipping it " +
                                "(configure moveToFolderPathOnFailure to move such messages aside; " +
                                "removing the '{Marker}*' category in Outlook retries it)",
                                subject, nodeConfig.MaxAttemptsPerMessage, AttemptCategoryPrefix);
                        }

                        continue;
                    }

                    var fromAddress = GetFromAddress(message);
                    if (!string.IsNullOrWhiteSpace(nodeConfig.SenderFilter) &&
                        (fromAddress == null || !fromAddress.Contains(nodeConfig.SenderFilter,
                            StringComparison.OrdinalIgnoreCase)))
                    {
                        continue;
                    }

                    var emailData = await BuildEmailDataAsync(accessToken, nodeConfig, messageId, message);

                    var batch = new EmailBatch
                    {
                        Emails = [emailData],
                        Count = 1,
                        ProcessedAt = DateTime.UtcNow
                    };

                    // AB#5126: one message → one execution, so the sender maps cleanly to a caller.
                    // The From address is the identifier; AB#5125 derives the per-message trust from
                    // the DKIM/DMARC (Authentication-Results) verdict — Strong only for a
                    // dkim=pass + aligned dmarc=pass mail, Weak otherwise (fail-safe when unknown).
                    var messageTrust = EmailMessageTrust.Evaluate(emailData.Authentication, fromAddress);
                    var sender = string.IsNullOrWhiteSpace(fromAddress)
                        ? null
                        : new ChannelSender(ChannelIdentifierKind.EmailAddress, fromAddress, messageTrust);
                    var binding = await callerBinder.BindAsync(context.TenantId, nodeConfig.CallerBinding, sender);
                    if (binding.Rejected)
                    {
                        logger.LogWarning("FromMicrosoftGraphEmail: {Reason} Skipping message '{MessageId}'.",
                            binding.RejectReason, messageId);
                        continue;
                    }

                    // Stamp the attempt BEFORE the run: a poison message can take the whole
                    // process down (OOM), in which case no catch block ever runs — only a
                    // marker persisted on the message itself makes that run count (AB#5142).
                    // Stamped after the binding gate, so a rejected message is never counted.
                    var stamped = await TrySetCategoriesAsync(accessToken, nodeConfig.Mailbox, messageId,
                        WithAttemptCategory(categories, attempts + 1));

                    object? executionResult;
                    try
                    {
                        // One pipeline run per message so the success/failure of a run maps
                        // 1:1 to the move decision for exactly that message.
                        executionResult = await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow)
                        {
                            VerifiedPrincipal = binding.Principal,
                            CallerTrust = binding.Trust
                        }, batch);
                    }
                    catch (OperationCanceledException) when (_cancellationTokenSource.Token.IsCancellationRequested)
                    {
                        // Adapter shutdown, not a message failure — unwind to the poll
                        // loop's cancellation handling without failure bookkeeping.
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // Remembers whether the marker for this attempt actually reached the
                        // mailbox: only then may a later poll read a lower stamped count as a
                        // deliberate operator reset rather than as a mailbox that cannot be
                        // stamped at all (AB#5260).
                        failureCounts[messageId] = new AttemptMemory(attempts + 1, stamped);
                        logger.LogError(ex,
                            "Pipeline run failed for mail '{Subject}' (attempt {Attempt}/{MaxAttempts}); message stays in '{Folder}'",
                            emailData.Subject, attempts + 1, nodeConfig.MaxAttemptsPerMessage, nodeConfig.FolderPath);
                        continue;
                    }

                    // AB#5345: returning is NOT importing. Where the pipeline was asked to confirm
                    // (successPath), only its confirmation counts as success — a run whose import
                    // branch stopped on a reported node error ends perfectly normally, and filing
                    // such a mail under "Done" is how a receipt disappears with nothing to show for
                    // it. An unconfirmed run is treated exactly like a failed one: the attempt is
                    // counted, the message is left where it is, and the parking machinery decides
                    // what happens once the budget is spent.
                    var confirmation = MailSuccessPath.Evaluate(nodeConfig.SuccessPath,
                        executionResult as JsonNode);
                    if (!MailSuccessPath.IsPostProcessingAllowed(confirmation))
                    {
                        failureCounts[messageId] = new AttemptMemory(attempts + 1, stamped);
                        logger.LogWarning(
                            "The run for mail '{Subject}' ended without setting '{SuccessPath}' to true — " +
                            "a node may have reported an error and stopped its branch while the execution " +
                            "still completed (attempt {Attempt}/{MaxAttempts}); the message stays in '{Folder}'",
                            emailData.Subject, nodeConfig.SuccessPath, attempts + 1,
                            nodeConfig.MaxAttemptsPerMessage, nodeConfig.FolderPath);
                        continue;
                    }

                    if (confirmation == MailRunConfirmation.NotConfigured && !_successPathWarningLogged)
                    {
                        _successPathWarningLogged = true;
                        logger.LogWarning(
                            "FromMicrosoftGraphEmail: no 'successPath' is configured, so a mail whose import " +
                            "branch stopped on a reported node error cannot be told apart from an imported one, " +
                            "and it is post-processed either way. Configure 'successPath' and have the pipeline " +
                            "write that flag as the last step of its import branch to make the post-processing " +
                            "conditional on the import itself.");
                    }

                    // Post-success bookkeeping runs OUTSIDE the failure net: a cleanup
                    // hiccup (or a shutdown cancel) after a successful run must never turn
                    // the success into a counted failed attempt. A failing move bubbles to
                    // the poll-level handler, which resets the folder ids and backs off.
                    failureCounts.Remove(messageId);
                    if (stamped)
                    {
                        await TryClearAttemptCategoriesAsync(accessToken, nodeConfig.Mailbox, messageId);
                    }

                    await ApplyPostProcessingAsync(accessToken, nodeConfig.Mailbox, messageId,
                        postProcessingMode, targetFolderId);

                    logger.LogInformation(
                        "Processed mail '{Subject}' from '{From}' ({AttachmentCount} attachments)",
                        emailData.Subject, fromAddress, emailData.Attachments.Count);
                }

                await Task.Delay(TimeSpan.FromSeconds(nodeConfig.PollingIntervalSeconds),
                    _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // Folder ids are re-resolved after connectivity errors (they may have been
                // renamed/moved, which surfaces as a request failure here).
                sourceFolderId = null;
                targetFolderId = null;
                failureFolderId = null;
                resolvedSourcePath = null;
                resolvedDonePath = null;
                resolvedFailedPath = null;
                logger.LogError(ex, "Error while polling Microsoft Graph mailbox '{Mailbox}'",
                    lastKnownMailbox);
                // Guard the backoff delay: a cancel during StopAsync makes Task.Delay throw
                // TaskCanceledException, which — being raised inside this catch — would escape
                // uncaught (the sibling catch (OperationCanceledException) does not cover it) and
                // fault the polling task, so the later StopAsync().WaitAsync() rethrows and the
                // trigger unregistration fails (AB#4761). Treat cancel as a clean loop exit.
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }
        }
    }

    /// <summary>
    /// The post-processing mode actually in force (AB#5345): the configured one, or — when none is
    /// configured — the one DERIVED from what this pipeline already said.
    /// </summary>
    /// <remarks>
    /// The derivation is the migration and it is exact: before AB#5345 this node did one thing
    /// after a run, move the message to the done folder (and park an exhausted one in the failure
    /// folder), and it did it only when such a folder was configured. So a configured done or
    /// failure folder means <see cref="MailPostProcessingMode.MoveToFolders" />.
    /// <para>
    /// 🔴 <b>With no folder at all there is NOTHING to derive, and this throws</b> (AB#5372). It used
    /// to answer <c>None</c> — "leave it in the source folder" — and that is not a working
    /// configuration: the mailbox is this trigger's only record of what it already imported, so a
    /// message left in the source folder is handed back to the next poll, for ever, while the mail
    /// behind the <c>maxMessagesPerPoll</c> cap is never reached. The import runs for ever, reports
    /// success and imports nothing new (AB#5336). In practice no live M365 tenant sits here — the
    /// settings page refuses to activate a channel without mailbox, source and done folder — but a
    /// hand-made pipeline could, and it would fail silently instead of on deploy.
    /// </para>
    /// </remarks>
    internal static MailPostProcessingMode ResolveEffectivePostProcessingMode(
        FromMicrosoftGraphEmailNodeConfiguration nodeConfig)
    {
        if (nodeConfig.PostProcessingMode.HasValue)
        {
            return nodeConfig.PostProcessingMode.Value;
        }

        if (!string.IsNullOrWhiteSpace(nodeConfig.MoveToFolderPathOnSuccess) ||
            !string.IsNullOrWhiteSpace(nodeConfig.MoveToFolderPathOnFailure))
        {
            return MailPostProcessingMode.MoveToFolders;
        }

        throw MeshAdapterPipelineExecutionException.MailPostProcessingModeNotConfigured(
            NodeType, HowToFix);
    }

    /// <summary>
    /// Carries out the mode's post-processing for one message whose import the pipeline CONFIRMED
    /// (AB#5345). Never called for a run that threw or that ended unconfirmed.
    /// </summary>
    /// <remarks>
    /// Mode B and C are Graph's counterparts of what the IMAP channel writes as <c>\Deleted</c>
    /// and <c>\Seen</c>: a delete (into Deleted Items — Graph's <c>DELETE /messages</c> is a
    /// soft delete, which is the recoverable half of "the mailbox is the queue"), and an
    /// <c>isRead</c> patch. Neither is allowed to fail the poll silently, so both report through
    /// their own log line rather than throwing into the message loop.
    /// </remarks>
    private async Task ApplyPostProcessingAsync(string accessToken, string mailbox, string messageId,
        MailPostProcessingMode mode, string? targetFolderId)
    {
        switch (mode)
        {
            case MailPostProcessingMode.MoveToFolders:
                if (targetFolderId != null)
                {
                    await MoveMessageAsync(accessToken, mailbox, messageId, targetFolderId);
                }

                return;

            case MailPostProcessingMode.Delete:
                await DeleteMessageAsync(accessToken, mailbox, messageId);
                return;

            case MailPostProcessingMode.MarkAsRead:
                await MarkMessageReadAsync(accessToken, mailbox, messageId);
                return;

            default:
                // AB#5372: `None` used to land here and do nothing, which is why a mailbox could be
                // polled for ever without ever changing. With it gone nothing reaches this branch —
                // ResolveEffectivePostProcessingMode returns one of the three or throws — so an
                // undefined value can only come from a cast, and saying so beats doing nothing.
                throw MeshAdapterPipelineExecutionException.InvalidValue(mode);
        }
    }

    /// <summary>
    /// Deletes a message (mode B). Graph's <c>DELETE</c> moves it to Deleted Items rather than
    /// erasing it, so a mistake is recoverable by the mailbox owner.
    /// </summary>
    private async Task DeleteMessageAsync(string accessToken, string mailbox, string messageId)
    {
        using var client = CreateGraphClient(accessToken);
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}";
        var response = await client.DeleteAsync(url, _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();
    }

    /// <summary>Marks a message as read (mode C).</summary>
    private async Task MarkMessageReadAsync(string accessToken, string mailbox, string messageId)
    {
        using var client = CreateGraphClient(accessToken);
        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}";
        var payload = JsonSerializer.Serialize(new { isRead = true });
        var response = await client.PatchAsync(url,
            new StringContent(payload, Encoding.UTF8, "application/json"), _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();
    }

    private async Task<string> GetAccessTokenAsync(GraphConfiguration config)
    {
        using var client = httpClientFactory.CreateClient();
        var tokenUrl = $"https://login.microsoftonline.com/{config.AzureTenantId}/oauth2/v2.0/token";

        var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        var response = await client.PostAsync(tokenUrl, content, _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token);
        var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// Resolves a '/'-separated folder path (relative to the mailbox root) to the folder id.
    /// With <paramref name="createLeafIfMissing"/> the LAST segment is created when absent —
    /// parent segments must exist.
    /// </summary>
    private async Task<string> ResolveFolderIdAsync(string accessToken, string mailbox, string folderPath,
        bool createLeafIfMissing)
    {
        using var client = CreateGraphClient(accessToken);

        var segments = folderPath.Split('/', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (segments.Length == 0)
        {
            throw new InvalidOperationException($"Mail folder path '{folderPath}' is empty");
        }

        string? parentId = null;
        for (var i = 0; i < segments.Length; i++)
        {
            var segment = segments[i];
            var escaped = segment.Replace("'", "''");
            var listUrl = parentId == null
                ? $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/mailFolders?$filter=displayName eq '{Uri.EscapeDataString(escaped)}'&$select=id"
                : $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/{parentId}/childFolders?$filter=displayName eq '{Uri.EscapeDataString(escaped)}'&$select=id";

            var response = await client.GetAsync(listUrl, _cancellationTokenSource!.Token);
            response.EnsureSuccessStatusCode();

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
            var matches = doc.RootElement.GetProperty("value");
            string? folderId = null;
            foreach (var f in matches.EnumerateArray())
            {
                folderId = f.GetProperty("id").GetString();
                break;
            }

            if (folderId == null && parentId == null)
            {
                // The first segment may address a well-known folder (archive, inbox, ...)
                // whose displayName is localized per mailbox language — try the
                // well-known-name route Graph offers for root folders.
                folderId = await TryGetWellKnownFolderIdAsync(client, mailbox, segment);
            }

            if (folderId == null)
            {
                var isLeaf = i == segments.Length - 1;
                if (!isLeaf || !createLeafIfMissing || parentId == null)
                {
                    var available = await ListFolderNamesAsync(client, mailbox, parentId);
                    throw new InvalidOperationException(
                        $"Mail folder '{segment}' (path '{folderPath}') not found in mailbox '{mailbox}'. " +
                        $"Available folders at this level: {available}. " +
                        "Note: Graph folder names may differ from the localized Outlook display " +
                        "(e.g. the archive folder is 'Archive' even when Outlook shows 'Archivieren'); " +
                        "well-known names like 'archive' or 'inbox' work for the first segment.");
                }

                folderId = await CreateChildFolderAsync(client, mailbox, parentId, segment);
                logger.LogInformation("Created mail folder '{Segment}' under path '{FolderPath}'", segment,
                    folderPath);
            }

            parentId = folderId;
        }

        return parentId!;
    }

    private async Task<string?> TryGetWellKnownFolderIdAsync(HttpClient client, string mailbox, string segment)
    {
        var wellKnownUrl =
            $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/{Uri.EscapeDataString(segment.ToLowerInvariant().Replace(" ", ""))}?$select=id";
        var response = await client.GetAsync(wellKnownUrl, _cancellationTokenSource!.Token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
        return doc.RootElement.TryGetProperty("id", out var id) ? id.GetString() : null;
    }

    /// <summary>
    /// Lists the folder display names at a level (root or child folders of a parent) so a
    /// failed path resolution can tell the user what the folders are actually called —
    /// Outlook shows localized names for the standard folders, Graph does not.
    /// </summary>
    private async Task<string> ListFolderNamesAsync(HttpClient client, string mailbox, string? parentId)
    {
        try
        {
            var listUrl = parentId == null
                ? $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/mailFolders?$top=100&$select=displayName"
                : $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/{parentId}/childFolders?$top=100&$select=displayName";
            var response = await client.GetAsync(listUrl, _cancellationTokenSource!.Token);
            if (!response.IsSuccessStatusCode)
            {
                return "(could not be listed)";
            }

            var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
            var names = doc.RootElement.TryGetProperty("value", out var values)
                ? values.EnumerateArray()
                    .Select(f => f.TryGetProperty("displayName", out var dn) ? dn.GetString() : null)
                    .Where(n => n != null)
                    .ToList()
                : [];
            return names.Count == 0 ? "(none)" : string.Join(", ", names.Select(n => $"'{n}'"));
        }
        catch
        {
            return "(could not be listed)";
        }
    }

    private async Task<string> CreateChildFolderAsync(HttpClient client, string mailbox, string parentId,
        string displayName)
    {
        var createUrl = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/mailFolders/{parentId}/childFolders";
        var payload = JsonSerializer.Serialize(new { displayName });
        var response = await client.PostAsync(createUrl,
            new StringContent(payload, Encoding.UTF8, "application/json"), _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
        return doc.RootElement.GetProperty("id").GetString()!;
    }

    /// <summary>
    ///     The <c>$select</c> the message query asks for.
    /// </summary>
    /// <remarks>
    ///     AB#5011: <c>internetMessageHeaders</c> is one of the properties Microsoft Graph returns
    ///     <b>only</b> when it is named in <c>$select</c> — which is the whole reason
    ///     <c>Authentication-Results</c> was not merely empty on the pipeline side but absent. Adding
    ///     it costs the full Received chain plus the DKIM signatures on every message, so it stays
    ///     opt-in and only the configured header names are surfaced downstream.
    /// </remarks>
    /// <param name="includeInternetMessageHeaders">Whether to request the internet message headers.</param>
    internal static string BuildMessageSelect(bool includeInternetMessageHeaders)
    {
        return "id,subject,from,toRecipients,receivedDateTime,body,hasAttachments,internetMessageId,categories"
               + (includeInternetMessageHeaders ? ",internetMessageHeaders" : string.Empty);
    }

    private async Task<List<JsonElement>> GetMessagesAsync(string accessToken,
        FromMicrosoftGraphEmailNodeConfiguration config, string folderId)
    {
        using var client = CreateGraphClient(accessToken);

        var select = BuildMessageSelect(config.IncludeInternetMessageHeaders);

        var url =
            $"{GraphBaseUrl}/users/{Uri.EscapeDataString(config.Mailbox)}/mailFolders/{folderId}/messages" +
            $"?$top={config.MaxMessagesPerPoll}&$orderby=receivedDateTime asc" +
            $"&$select={select}";

        var response = await client.GetAsync(url, _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
        var messages = new List<JsonElement>();
        if (doc.RootElement.TryGetProperty("value", out var values))
        {
            foreach (var msg in values.EnumerateArray())
            {
                messages.Add(msg.Clone());
            }
        }

        return messages;
    }

    private async Task<EmailData> BuildEmailDataAsync(string accessToken,
        FromMicrosoftGraphEmailNodeConfiguration config, string messageId, JsonElement message)
    {
        var mailbox = config.Mailbox;
        var subject = message.TryGetProperty("subject", out var subj) ? subj.GetString() : null;
        var fromAddress = GetFromAddress(message);
        var fromName = GetFromName(message);
        var receivedAt = message.TryGetProperty("receivedDateTime", out var dt)
            ? dt.GetDateTime()
            : DateTime.UtcNow;

        string? bodyContent = null;
        var bodyIsHtml = false;
        if (message.TryGetProperty("body", out var body))
        {
            bodyContent = body.TryGetProperty("content", out var content) ? content.GetString() : null;
            bodyIsHtml = body.TryGetProperty("contentType", out var ct) &&
                         string.Equals(ct.GetString(), "html", StringComparison.OrdinalIgnoreCase);
        }

        var to = message.TryGetProperty("toRecipients", out var toRecipients)
            ? string.Join("; ", toRecipients.EnumerateArray()
                .Select(r => r.TryGetProperty("emailAddress", out var ea) &&
                             ea.TryGetProperty("address", out var addr)
                    ? addr.GetString()
                    : null)
                .Where(a => a != null))
            : null;

        // AB#4647: Graph's `hasAttachments` flag reflects only regular (non-inline)
        // attachments, so an inline-only mail — e.g. a receipt photo pasted into the
        // body via a cid: reference — reports hasAttachments=false even though the
        // image is retrievable from the /attachments endpoint. Gating on the flag
        // dropped those images and the pipeline rendered the mail body instead.
        // Always query the endpoint; GetAttachmentsAsync returns an empty list
        // cheaply when there is nothing to fetch.
        var attachments = await GetAttachmentsAsync(accessToken, mailbox, messageId);

        var emailData = new EmailData
        {
            Subject = subject,
            From = string.IsNullOrWhiteSpace(fromName) ? fromAddress : $"{fromName} <{fromAddress}>",
            FromAddress = fromAddress,
            To = to,
            Date = receivedAt,
            Body = bodyContent,
            HtmlBody = bodyIsHtml ? bodyContent : null,
            TextBody = bodyIsHtml ? null : bodyContent,
            MessageId = message.TryGetProperty("internetMessageId", out var imi) ? imi.GetString() : messageId,
            Attachments = attachments
        };

        if (config.IncludeInternetMessageHeaders)
        {
            ApplyInternetMessageHeaders(emailData, message, config.InternetMessageHeaderNames);
        }

        return emailData;
    }

    /// <summary>
    ///     Header names surfaced when the node was not told which ones it wants. The set a sender gate
    ///     needs and nothing else — the rest of the headers are kilobytes of Received chain and base64
    ///     signatures that would land in the pipeline data context of every message. AB#5011.
    /// </summary>
    internal static readonly string[] DefaultInternetMessageHeaderNames =
    [
        AuthenticationResultsParser.HeaderName,
        "Authentication-Results-Original",
        "ARC-Authentication-Results",
        "Received-SPF"
    ];

    /// <summary>
    ///     Copies the selected internet message headers onto <paramref name="emailData" /> and parses
    ///     the SPF/DKIM/DMARC verdicts out of <c>Authentication-Results</c>. AB#5011.
    /// </summary>
    /// <remarks>
    ///     🔴 <b>Only the first occurrence of a name is kept</b>, and the verdicts are parsed from the
    ///     first <c>Authentication-Results</c> header alone. A sender can put such a header into the
    ///     message they submit and the receiving server <i>prepends</i> its own rather than replacing
    ///     it, so only the topmost one was written by infrastructure we trust. Joining the occurrences
    ///     — the obvious way to "not lose data" — would put a forged <c>dmarc=pass</c> into the same
    ///     string as the real verdict, where any downstream substring or regex check would find it.
    ///     The number of occurrences is reported on
    ///     <see cref="EmailAuthenticationResults.HeaderCount" /> instead, so a pipeline can treat a
    ///     duplicated header as the anomaly it is.
    ///     <para>
    ///         Graph returns the headers in message order, i.e. most recently added first, which is why
    ///         "first wins" is the right rule here and not merely the cheap one.
    ///     </para>
    /// </remarks>
    internal static void ApplyInternetMessageHeaders(EmailData emailData, JsonElement message,
        string[]? configuredNames)
    {
        if (!message.TryGetProperty("internetMessageHeaders", out var headers) ||
            headers.ValueKind != JsonValueKind.Array)
        {
            // Graph omits the property entirely for a message that carries no internet headers (an
            // internally generated or draft mail). Not an error, and deliberately not an empty
            // Authentication record either: "no header" must stay distinguishable from "header said
            // nothing", or a gate cannot tell "unknown" from "reported as none".
            return;
        }

        // Authentication-Results is always collected, whatever the name filter says: it is what the
        // verdicts are parsed from, and a list that omitted it would turn them off silently while the
        // flag claims they are on.
        var wanted = new HashSet<string>(
            configuredNames is { Length: > 0 } ? configuredNames : DefaultInternetMessageHeaderNames,
            StringComparer.OrdinalIgnoreCase) { AuthenticationResultsParser.HeaderName };

        string? authenticationResults = null;
        var authenticationResultsCount = 0;

        foreach (var header in headers.EnumerateArray())
        {
            var name = header.TryGetProperty("name", out var n) ? n.GetString() : null;
            if (string.IsNullOrEmpty(name))
            {
                continue;
            }

            var value = header.TryGetProperty("value", out var v) ? v.GetString() ?? string.Empty : string.Empty;

            if (string.Equals(name, AuthenticationResultsParser.HeaderName, StringComparison.OrdinalIgnoreCase))
            {
                authenticationResultsCount++;
                authenticationResults ??= value;
            }

            if (!wanted.Contains(name))
            {
                continue;
            }

            // TryAdd, not the indexer: first occurrence wins — see the remarks.
            emailData.Headers.TryAdd(name, value);
        }

        if (authenticationResultsCount > 0)
        {
            emailData.Authentication =
                AuthenticationResultsParser.Parse(authenticationResults, authenticationResultsCount);
        }
    }

    private async Task<List<AttachmentData>> GetAttachmentsAsync(string accessToken, string mailbox,
        string messageId)
    {
        using var client = CreateGraphClient(accessToken);

        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}/attachments";
        var response = await client.GetAsync(url, _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();

        var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
        var attachments = new List<AttachmentData>();

        if (!doc.RootElement.TryGetProperty("value", out var values))
        {
            return attachments;
        }

        foreach (var att in values.EnumerateArray())
        {
            // Only file attachments carry contentBytes; item/reference attachments
            // (attached mails, OneDrive links) are skipped.
            var odataType = att.TryGetProperty("@odata.type", out var ot) ? ot.GetString() : null;
            if (odataType != "#microsoft.graph.fileAttachment")
            {
                logger.LogDebug("Skipping non-file attachment of type {OdataType} on message {MessageId}",
                    odataType, messageId);
                continue;
            }

            if (!att.TryGetProperty("contentBytes", out var contentBytes) ||
                contentBytes.GetString() == null)
            {
                continue;
            }

            var data = contentBytes.GetString()!;
            var fileName = att.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";
            var rawContentType = att.TryGetProperty("contentType", out var ct)
                ? ct.GetString() ?? "application/octet-stream"
                : "application/octet-stream";

            // AB#4433: S/MIME-signed senders (e.g. Magenta) deliver the whole mail as a
            // single "smime.p7m" PKCS#7 container; the real PDF invoice is encapsulated
            // inside the signed envelope, so the adapter otherwise sees no PDF and renders
            // the mail body instead. Unwrap the container and surface the inner PDFs as
            // normal attachments. On any failure we fall through and keep the raw p7m
            // (pre-AB#4433 behaviour) rather than dropping the poll.
            if (IsSmimeContainer(fileName, rawContentType))
            {
                if (TryExtractSmimePdfAttachments(data, out var smimePdfs) && smimePdfs.Count > 0)
                {
                    attachments.AddRange(smimePdfs);
                    logger.LogInformation(
                        "Extracted {Count} PDF attachment(s) from an S/MIME container on message {MessageId}",
                        smimePdfs.Count, messageId);
                    continue;
                }

                logger.LogWarning(
                    "Could not surface a PDF from the S/MIME container on message {MessageId} " +
                    "(no inline signed content, no PDF part, or a parse failure); keeping the raw attachment",
                    messageId);
            }

            attachments.Add(new AttachmentData
            {
                FileName = fileName,
                // AB#4433: many senders deliver a PDF invoice with a generic
                // contentType (e.g. application/octet-stream). Normalize to
                // application/pdf when the file-name extension or the %PDF- magic
                // header says so, otherwise HasPdfAttachment and the pipeline's
                // ContentType filter both miss the real attachment and the mail
                // body gets rendered as the receipt instead of the invoice.
                ContentType = AttachmentContentType.NormalizePdf(fileName, rawContentType, data),
                Data = data,
                // AB#4647: surface the inline flag so the pipeline can reason about
                // embedded images (e.g. a receipt photo referenced via cid:).
                IsInline = att.TryGetProperty("isInline", out var inl) &&
                           inl.ValueKind == JsonValueKind.True,
                Length = att.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue)
                    ? sizeValue
                    : (long)(data.Length * 0.75)
            });
        }

        return attachments;
    }

    private static readonly HashSet<string> SmimeContentTypes = new(StringComparer.OrdinalIgnoreCase)
    {
        "multipart/signed",
        "application/pkcs7-mime",
        "application/x-pkcs7-mime",
        "application/pkcs7-signature",
        "application/x-pkcs7-signature"
    };

    /// <summary>
    /// True when an attachment is an S/MIME (PKCS#7) container rather than a real
    /// document — either by the canonical <c>smime.p7m</c> file name or by one of the
    /// S/MIME content types. AB#4433.
    /// </summary>
    private static bool IsSmimeContainer(string fileName, string contentType)
    {
        return string.Equals(fileName, "smime.p7m", StringComparison.OrdinalIgnoreCase)
               || SmimeContentTypes.Contains(contentType);
    }

    /// <summary>
    /// Unwraps an S/MIME container attachment into its PDF invoices. The mails we see
    /// are SIGNED (not encrypted), so the original MIME is recoverable WITHOUT any
    /// certificate or private key. Two wire formats exist and BOTH are handled:
    /// <list type="number">
    /// <item>Opaque PKCS#7 signed-data (DER): <see cref="SignedCms"/> decodes the
    /// container and hands back the encapsulated MIME, which MimeKit then parses.</item>
    /// <item>Clear-signed <c>multipart/signed</c> MIME: the raw bytes ARE a MIME entity
    /// whose FIRST child is the original content (second child is the detached
    /// pkcs7-signature). Parsed directly with MimeKit; no CMS decode involved.</item>
    /// </list>
    /// The extracted parts run through <see cref="AttachmentContentType.NormalizePdf"/> as well
    /// (the inner PDF may itself be a mislabeled octet-stream). Returns true (with the
    /// PDFs in <paramref name="pdfs"/>) when at least one PDF was surfaced; on an
    /// encrypted container, a PDF-less body, or ANY parse failure returns false with an
    /// empty list so the caller keeps the raw container (pre-AB#4433 behaviour) instead
    /// of dropping the poll. Pure/static for unit testing (InternalsVisibleTo). AB#4433.
    /// </summary>
    internal static bool TryExtractSmimePdfAttachments(string base64Container, out List<AttachmentData> pdfs)
    {
        pdfs = new List<AttachmentData>();

        byte[] raw;
        try
        {
            raw = Convert.FromBase64String(base64Container);
        }
        catch (FormatException)
        {
            return false;
        }

        // Attempt 1 — opaque PKCS#7 signed-data: the original MIME entity is the
        // encapsulated content. Decode + ContentInfo.Content extracts it without
        // verifying the signature or needing any key. (Detached/encrypted variants
        // yield no inline content; a clear-signed MIME container is not DER at all
        // and makes Decode throw — both fall through to attempt 2.)
        try
        {
            var signedCms = new SignedCms();
            signedCms.Decode(raw);
            var innerBytes = signedCms.ContentInfo.Content;
            if (innerBytes.Length > 0)
            {
                using var innerStream = new MemoryStream(innerBytes);
                var entity = MimeEntity.Load(innerStream, CancellationToken.None);
                CollectPdfLeafParts(entity, pdfs);
                if (pdfs.Count > 0)
                {
                    return true;
                }
            }
        }
        catch
        {
            // Not opaque CMS — try the clear-signed MIME shape next.
        }

        // Attempt 2 — clear-signed multipart/signed: the raw bytes are themselves a
        // MIME entity (Content-Type: multipart/signed) whose first child is the
        // original, readable content and whose second child is the detached
        // pkcs7-signature. Signature verification is deliberately skipped — we only
        // need the content, and the signature part can never pass the PDF filter, so
        // the whole entity is walked (also covers other multipart layouts).
        try
        {
            using var rawStream = new MemoryStream(raw);
            var entity = MimeEntity.Load(rawStream, CancellationToken.None);
            CollectPdfLeafParts(entity, pdfs);
            if (pdfs.Count > 0)
            {
                return true;
            }
        }
        catch
        {
            // Not a bare MIME entity either — last resort: a full rfc822 message.
        }

        try
        {
            using var rawStream = new MemoryStream(raw);
            var message = MimeMessage.Load(rawStream, CancellationToken.None);
            if (message.Body != null)
            {
                CollectPdfLeafParts(message.Body, pdfs);
            }
        }
        catch
        {
            // Malformed container / encrypted / not MIME at all — never let an
            // unexpected attachment abort the mailbox poll; the raw attachment is kept.
        }

        if (pdfs.Count > 0)
        {
            return true;
        }

        pdfs = new List<AttachmentData>();
        return false;
    }

    /// <summary>
    /// Walks a MIME entity and appends every leaf part that is (or normalizes to) a PDF
    /// to <paramref name="pdfs"/>. AB#4433.
    /// </summary>
    private static void CollectPdfLeafParts(MimeEntity entity, List<AttachmentData> pdfs)
    {
        foreach (var part in EnumerateLeafParts(entity))
        {
            if (part.Content == null)
            {
                continue;
            }

            var name = part.FileName ?? part.ContentType.Name ?? "attachment";
            using var content = new MemoryStream();
            part.Content.DecodeTo(content);
            var bytes = content.ToArray();
            var base64 = Convert.ToBase64String(bytes);
            var normalized = AttachmentContentType.NormalizePdf(name, part.ContentType.MimeType, base64);
            if (!string.Equals(normalized, "application/pdf", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            pdfs.Add(new AttachmentData
            {
                FileName = name,
                ContentType = normalized,
                Data = base64,
                Length = bytes.Length
            });
        }
    }

    /// <summary>
    /// Depth-first walk yielding every leaf <see cref="MimePart"/> of a MIME entity
    /// (recursing into <see cref="Multipart"/> and encapsulated <see cref="MessagePart"/>).
    /// </summary>
    private static IEnumerable<MimePart> EnumerateLeafParts(MimeEntity entity)
    {
        switch (entity)
        {
            case Multipart multipart:
                foreach (var child in multipart)
                {
                    foreach (var leaf in EnumerateLeafParts(child))
                    {
                        yield return leaf;
                    }
                }

                break;
            case MessagePart messagePart when messagePart.Message?.Body != null:
                foreach (var leaf in EnumerateLeafParts(messagePart.Message.Body))
                {
                    yield return leaf;
                }

                break;
            case MimePart part:
                yield return part;
                break;
        }
    }

    // Outlook category carrying the persistent per-message attempt count, e.g.
    // "OctoMesh-Import-Attempt-2". Deliberately a category (not an extended
    // property): a category is visible in the mailbox, so an operator can see at a
    // glance why a message was skipped or moved aside. AB#5142.
    //
    // 🔴 "Visible" only holds once the NAME is registered in the mailbox's master
    // category list — Outlook and OWA render nothing for a category a message carries
    // but the mailbox does not know, which is exactly what AB#5142 shipped and what
    // left the operator on prod-1 unable to find (let alone clear) the marker. The
    // registration is done by TryRegisterImportCategoriesAsync. AB#5260.
    internal const string AttemptCategoryPrefix = "OctoMesh-Import-Attempt-";

    // Outlook category marking a message the adapter gave up on and parked in the
    // failure folder. It is the counterpart of the attempt markers and never coexists
    // with them: the two-state protocol is what makes "move the mail back into the
    // source folder" readable as a reset — a parked marker seen in the SOURCE folder
    // can only have got there by hand. AB#5260.
    internal const string FailedCategory = "OctoMesh-Import-Failed";

    // Outlook preset colours the markers are registered with: the attempt markers in
    // orange ("in trouble, still trying"), the parked marker in red ("stopped"), so the
    // two states are distinguishable at a glance in the message list. AB#5260.
    private const string AttemptCategoryColour = "preset1";
    private const string FailedCategoryColour = "preset0";

    /// <summary>
    /// True when a category is a well-formed attempt marker (prefix plus a numeric
    /// suffix). Only these are ever counted or removed — a category that merely
    /// shares the prefix is user metadata and is preserved.
    /// </summary>
    private static bool IsAttemptCategory(string category, out int attempts)
    {
        attempts = 0;
        // The marker is a persisted protocol between adapter runs: plain ASCII digits,
        // no sign, no whitespace, culture-invariant — anything else is user metadata.
        return category.StartsWith(AttemptCategoryPrefix, StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(category.AsSpan(AttemptCategoryPrefix.Length), NumberStyles.None,
                   CultureInfo.InvariantCulture, out attempts);
    }

    /// <summary>
    /// Reads the attempt count from a message's categories. Multiple markers (which
    /// only a partial category update failure could leave behind) read as the maximum.
    /// </summary>
    internal static int GetAttemptCount(IReadOnlyList<string> categories)
    {
        var attempts = 0;
        foreach (var category in categories)
        {
            if (IsAttemptCategory(category, out var value) && value > attempts)
            {
                attempts = value;
            }
        }

        return attempts;
    }

    /// <summary>
    /// Returns the categories with the attempt marker set to <paramref name="attempts"/>,
    /// preserving every unrelated (user-assigned) category. The parked marker is dropped
    /// as well: a message being tried again is by definition no longer parked, and leaving
    /// it behind would make the next poll read the message as freshly moved back and hand
    /// it yet another full attempt budget (AB#5260).
    /// </summary>
    internal static List<string> WithAttemptCategory(IReadOnlyList<string> categories, int attempts)
    {
        var result = WithoutImportMarkers(categories);
        result.Add($"{AttemptCategoryPrefix}{attempts}");
        return result;
    }

    /// <summary>
    /// Returns the categories with every attempt marker replaced by the single parked
    /// marker (<see cref="FailedCategory"/>), preserving every unrelated (user-assigned)
    /// category. The attempt count is not kept: once a message is parked the only number
    /// that matters is "gave up", and a parked message that is moved back starts over.
    /// AB#5260.
    /// </summary>
    internal static List<string> WithFailedCategory(IReadOnlyList<string> categories)
    {
        var result = WithoutImportMarkers(categories);
        result.Add(FailedCategory);
        return result;
    }

    /// <summary>
    /// Returns the categories with every well-formed attempt marker removed,
    /// preserving every unrelated (user-assigned) category — including one that
    /// merely shares the prefix without a numeric suffix.
    /// </summary>
    internal static List<string> WithoutAttemptCategory(IReadOnlyList<string> categories)
    {
        return categories
            .Where(c => !IsAttemptCategory(c, out _))
            .ToList();
    }

    /// <summary>
    /// Returns the categories with every marker this node owns removed — the attempt
    /// markers and the parked marker — preserving every unrelated (user-assigned)
    /// category. This is what a reset writes. AB#5260.
    /// </summary>
    internal static List<string> WithoutImportMarkers(IReadOnlyList<string> categories)
    {
        return categories
            .Where(c => !IsAttemptCategory(c, out _) && !IsFailedCategory(c))
            .ToList();
    }

    /// <summary>
    /// True when the message carries the parked marker. Seen on a message in the SOURCE
    /// folder this means an operator moved the message back out of the failure folder —
    /// the adapter only ever stamps it while moving a message the other way. AB#5260.
    /// </summary>
    internal static bool HasFailedCategory(IReadOnlyList<string> categories)
    {
        return categories.Any(IsFailedCategory);
    }

    private static bool IsFailedCategory(string category)
    {
        return string.Equals(category, FailedCategory, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// What the poll loop remembers in process about a message whose pipeline run failed.
    /// <see cref="Stamped"/> records whether the marker for <see cref="Attempts"/> actually
    /// reached the mailbox — without it a lower stamped count cannot be told apart from a
    /// mailbox that never accepted a stamp in the first place. AB#5260.
    /// </summary>
    internal readonly record struct AttemptMemory(int Attempts, bool Stamped);

    /// <summary>
    /// The attempt count a message is judged by, combining the marker on the message with
    /// what the poll loop remembers in process.
    /// </summary>
    /// <remarks>
    /// The marker is authoritative <b>whenever it is being written successfully</b>: nobody
    /// but a human clears an Outlook category, so a stamped count below the remembered one
    /// is a deliberate reset and <paramref name="operatorReset"/> tells the caller to forget
    /// what it remembered. AB#5142 combined the two with a plain <c>Math.Max</c>, which made
    /// the in-process dictionary out-vote the operator: clearing the category changed nothing
    /// until the adapter restarted or the DataFlow was redeployed (AB#5260).
    /// <para>
    /// The in-process count stays what it was documented to be — the fallback for a mailbox
    /// whose stamp write failed. For such a message (<c>Stamped == false</c>) the marker says
    /// nothing, so it can never trigger a reset and the remembered count keeps counting. That
    /// also covers the mixed case where earlier attempts stamped fine and a later one did not:
    /// no attempt is silently lost, at the price of the category route not resetting a message
    /// in a mailbox that just stopped accepting stamps — where clearing the marker by hand
    /// would not have stuck either.
    /// </para>
    /// </remarks>
    internal static int ResolveAttemptCount(IReadOnlyList<string> categories, AttemptMemory? remembered,
        out bool operatorReset)
    {
        operatorReset = false;
        var stampedAttempts = GetAttemptCount(categories);

        if (remembered is not { } memory)
        {
            return stampedAttempts;
        }

        if (memory.Stamped && stampedAttempts < memory.Attempts)
        {
            operatorReset = true;
            return stampedAttempts;
        }

        return Math.Max(stampedAttempts, memory.Attempts);
    }

    private static IReadOnlyList<string> GetCategories(JsonElement message)
    {
        if (!message.TryGetProperty("categories", out var categories) ||
            categories.ValueKind != JsonValueKind.Array)
        {
            return [];
        }

        return categories.EnumerateArray()
            .Select(c => c.GetString())
            .Where(c => c != null)
            .Select(c => c!)
            .ToList();
    }

    /// <summary>
    /// Removes every import marker from a message using the message's CURRENT
    /// categories — the pipeline run between stamping and clearing can take a
    /// while, and clearing from the poll-time snapshot would silently revert any
    /// category a user assigned in the meantime. Never throws (a leftover marker
    /// on a successfully imported message is cosmetic; the mail leaves the source
    /// folder anyway).
    /// </summary>
    private async Task TryClearAttemptCategoriesAsync(string accessToken, string mailbox, string messageId)
    {
        try
        {
            using var client = CreateGraphClient(accessToken);
            var getUrl =
                $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}?$select=categories";
            var response = await client.GetAsync(getUrl, _cancellationTokenSource!.Token);
            response.EnsureSuccessStatusCode();
            using var doc = JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
            var current = GetCategories(doc.RootElement);

            await TrySetCategoriesAsync(accessToken, mailbox, messageId, WithoutImportMarkers(current));
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not clear the attempt-tracking category on message {MessageId}; the marker stays behind",
                messageId);
        }
    }

    /// <summary>
    /// Replaces a message's categories. Never throws: a mailbox where the stamp cannot
    /// be written degrades to the in-process attempt counter instead of blocking the
    /// import (the caller checks the return value before relying on the stamp).
    /// </summary>
    private async Task<bool> TrySetCategoriesAsync(string accessToken, string mailbox, string messageId,
        List<string> categories)
    {
        try
        {
            using var client = CreateGraphClient(accessToken);
            var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}";
            var payload = JsonSerializer.Serialize(new { categories });
            var response = await client.PatchAsync(url,
                new StringContent(payload, Encoding.UTF8, "application/json"), _cancellationTokenSource!.Token);
            response.EnsureSuccessStatusCode();
            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex,
                "Could not update the attempt-tracking categories on message {MessageId}; " +
                "attempt counting falls back to in-process tracking until the adapter restarts",
                messageId);
            return false;
        }
    }

    /// <summary>
    /// Moves a message to another folder and returns the id of the moved message.
    /// Graph mints a NEW id for the moved copy, so anything that still wants to touch
    /// that message — stamping the parked marker, for one — must use the returned id
    /// and not the one the poll read (AB#5260). Falls back to the original id if the
    /// response carries none.
    /// </summary>
    private async Task<string> MoveMessageAsync(string accessToken, string mailbox, string messageId,
        string destinationFolderId)
    {
        using var client = CreateGraphClient(accessToken);

        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}/move";
        var payload = JsonSerializer.Serialize(new { destinationId = destinationFolderId });
        var response = await client.PostAsync(url,
            new StringContent(payload, Encoding.UTF8, "application/json"), _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();

        try
        {
            using var doc =
                JsonDocument.Parse(await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
            return doc.RootElement.TryGetProperty("id", out var id) && id.GetString() is { } movedId
                ? movedId
                : messageId;
        }
        catch (JsonException)
        {
            return messageId;
        }
    }

    /// <summary>
    /// Registers the import markers in the mailbox's master category list so Outlook and
    /// OWA actually render them — a category a message carries but the mailbox does not
    /// know is invisible in the UI, which is why the marker AB#5142 introduced "so an
    /// operator can see at a glance why a message was skipped" could not be found (let
    /// alone removed) by the operator it was meant for. AB#5260.
    /// </summary>
    /// <remarks>
    /// Idempotent by construction: the existing names are read first and only the missing
    /// ones are created, and a create that races another writer (Graph answers
    /// <c>ErrorCategoryNameAlreadyExists</c>) is treated as success.
    /// <para>
    /// Needs the <c>MailboxSettings.ReadWrite</c> Graph scope, which an existing app
    /// registration may well not have. That is not an error: the markers still do their
    /// job as attempt tracking, they are merely invisible, so a <c>403</c> degrades to one
    /// warning and the import carries on. Returns whether the node should stop asking —
    /// true after success and after a permission refusal (both are final), false for a
    /// transient failure so the next poll tries again.
    /// </para>
    /// </remarks>
    private async Task<bool> TryRegisterImportCategoriesAsync(string accessToken, string mailbox, int maxAttempts)
    {
        try
        {
            using var client = CreateGraphClient(accessToken);
            var listUrl =
                $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/outlook/masterCategories?$select=displayName";
            var listResponse = await client.GetAsync(listUrl, _cancellationTokenSource!.Token);
            if (listResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
            {
                LogMasterCategoriesForbidden(mailbox);
                return true;
            }

            listResponse.EnsureSuccessStatusCode();

            using var doc =
                JsonDocument.Parse(await listResponse.Content.ReadAsStringAsync(_cancellationTokenSource.Token));
            var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            if (doc.RootElement.TryGetProperty("value", out var values))
            {
                foreach (var category in values.EnumerateArray())
                {
                    if (category.TryGetProperty("displayName", out var name) && name.GetString() is { } displayName)
                    {
                        existing.Add(displayName);
                    }
                }
            }

            var created = 0;
            foreach (var (name, colour) in ImportCategoryDefinitions(maxAttempts))
            {
                if (existing.Contains(name))
                {
                    continue;
                }

                var createUrl = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/outlook/masterCategories";
                var payload = JsonSerializer.Serialize(new { displayName = name, color = colour });
                var createResponse = await client.PostAsync(createUrl,
                    new StringContent(payload, Encoding.UTF8, "application/json"), _cancellationTokenSource.Token);

                if (createResponse.StatusCode == System.Net.HttpStatusCode.Forbidden)
                {
                    LogMasterCategoriesForbidden(mailbox);
                    return true;
                }

                if (createResponse.IsSuccessStatusCode)
                {
                    created++;
                    continue;
                }

                // A name that already exists comes back as 409 (or 400 carrying
                // ErrorCategoryNameAlreadyExists) — the goal state, not a failure.
                var body = await createResponse.Content.ReadAsStringAsync(_cancellationTokenSource.Token);
                if (createResponse.StatusCode == System.Net.HttpStatusCode.Conflict ||
                    body.Contains("ErrorCategoryNameAlreadyExists", StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                createResponse.EnsureSuccessStatusCode();
            }

            if (created > 0)
            {
                logger.LogInformation(
                    "Registered {Count} import marker categor(ies) in the master category list of mailbox " +
                    "'{Mailbox}' so they are visible and removable in Outlook/OWA",
                    created, mailbox);
            }

            return true;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Transient (connectivity, throttling): retried on the next poll. Never fatal —
            // an unregistered marker is a visibility problem, not an import problem.
            logger.LogWarning(ex,
                "Could not register the import marker categories in mailbox '{Mailbox}'; " +
                "the markers keep working but stay invisible in Outlook until this succeeds",
                mailbox);
            return false;
        }
    }

    private void LogMasterCategoriesForbidden(string mailbox)
    {
        logger.LogWarning(
            "The Graph app registration may not write the master category list of mailbox '{Mailbox}' " +
            "(needs MailboxSettings.ReadWrite). The import attempt markers ('{AttemptMarker}N', " +
            "'{FailedMarker}') keep working, but Outlook/OWA will not render them — grant the scope to " +
            "make them visible and removable for operators",
            mailbox, AttemptCategoryPrefix, FailedCategory);
    }

    /// <summary>
    /// The marker names that must exist in a mailbox's master category list, with the
    /// colour each is registered in. One entry per reachable attempt count plus the parked
    /// marker — the set is bounded by <c>MaxAttemptsPerMessage</c> and tiny. AB#5260.
    /// </summary>
    internal static IEnumerable<(string Name, string Colour)> ImportCategoryDefinitions(int maxAttempts)
    {
        for (var attempt = 1; attempt <= maxAttempts; attempt++)
        {
            yield return ($"{AttemptCategoryPrefix}{attempt}", AttemptCategoryColour);
        }

        yield return (FailedCategory, FailedCategoryColour);
    }

    private HttpClient CreateGraphClient(string accessToken)
    {
        var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        return client;
    }

    private static string? GetFromAddress(JsonElement message)
    {
        if (message.TryGetProperty("from", out var from) &&
            from.TryGetProperty("emailAddress", out var emailAddress) &&
            emailAddress.TryGetProperty("address", out var address))
        {
            return address.GetString();
        }

        return null;
    }

    private static string? GetFromName(JsonElement message)
    {
        if (message.TryGetProperty("from", out var from) &&
            from.TryGetProperty("emailAddress", out var emailAddress) &&
            emailAddress.TryGetProperty("name", out var name))
        {
            return name.GetString();
        }

        return null;
    }
}
