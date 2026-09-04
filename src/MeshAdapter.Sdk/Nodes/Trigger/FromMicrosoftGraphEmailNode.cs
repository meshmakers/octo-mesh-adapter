using System.Net.Http.Headers;
using System.Security.Cryptography.Pkcs;
using System.Text;
using System.Text.Json;
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

    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;

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

        if (!string.IsNullOrWhiteSpace(c.SettingsConfiguration))
        {
            logger.LogInformation(
                "FromMicrosoftGraphEmail: resolved mailbox/folders from settings configuration '{Settings}' (folder='{Folder}', moveTo='{MoveTo}')",
                c.SettingsConfiguration, effectiveConfig.FolderPath, effectiveConfig.MoveToFolderPathOnSuccess);
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

        _cancellationTokenSource = new CancellationTokenSource();
        _pollingTask = Task.Run(
            async () => await PollForMessagesAsync(context, graphConfig, effectiveConfig),
            _cancellationTokenSource.Token);

        return Task.CompletedTask;
    }

    /// <summary>
    /// Returns a copy of <paramref name="c"/> with Mailbox / FolderPath /
    /// MoveToFolderPathOnSuccess / PollingIntervalSeconds resolved from the optional
    /// settings configuration (well-known name <see cref="FromMicrosoftGraphEmailNodeConfiguration.SettingsConfiguration"/>).
    /// A non-empty settings value overrides the corresponding node property; anything
    /// missing falls back to the node property. The node stays domain-agnostic: which
    /// attributes to read is given by the *Attribute node properties.
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
            PollingIntervalSeconds =
                ConfigurationSettingsReader.ReadPositiveInt(attrs, c.PollingSecondsAttribute) ?? c.PollingIntervalSeconds,
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
        FromMicrosoftGraphEmailNodeConfiguration nodeConfig)
    {
        string? sourceFolderId = null;
        string? targetFolderId = null;
        // Messages that keep failing are skipped after MaxAttemptsPerMessage tries so a
        // poison message cannot block the folder queue; successful messages are moved
        // away, so no bookkeeping is needed for them.
        var failureCounts = new Dictionary<string, int>();

        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                var accessToken = await GetAccessTokenAsync(graphConfig);

                sourceFolderId ??= await ResolveFolderIdAsync(accessToken, nodeConfig.Mailbox,
                    nodeConfig.FolderPath, createLeafIfMissing: false);
                if (!string.IsNullOrWhiteSpace(nodeConfig.MoveToFolderPathOnSuccess))
                {
                    targetFolderId ??= await ResolveFolderIdAsync(accessToken, nodeConfig.Mailbox,
                        nodeConfig.MoveToFolderPathOnSuccess, createLeafIfMissing: true);
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

                    if (failureCounts.TryGetValue(messageId, out var attempts) &&
                        attempts >= nodeConfig.MaxAttemptsPerMessage)
                    {
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

                    try
                    {
                        // One pipeline run per message so the success/failure of a run maps
                        // 1:1 to the move decision for exactly that message.
                        await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow)
                        {
                            VerifiedPrincipal = binding.Principal,
                            CallerTrust = binding.Trust
                        }, batch);

                        failureCounts.Remove(messageId);

                        if (targetFolderId != null)
                        {
                            await MoveMessageAsync(accessToken, nodeConfig.Mailbox, messageId, targetFolderId);
                        }

                        logger.LogInformation(
                            "Processed mail '{Subject}' from '{From}' ({AttachmentCount} attachments)",
                            emailData.Subject, fromAddress, emailData.Attachments.Count);
                    }
                    catch (Exception ex)
                    {
                        var count = failureCounts.GetValueOrDefault(messageId) + 1;
                        failureCounts[messageId] = count;
                        logger.LogError(ex,
                            "Pipeline run failed for mail '{Subject}' (attempt {Attempt}/{MaxAttempts}); message stays in '{Folder}'",
                            emailData.Subject, count, nodeConfig.MaxAttemptsPerMessage, nodeConfig.FolderPath);
                    }
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
                logger.LogError(ex, "Error while polling Microsoft Graph mailbox '{Mailbox}'", nodeConfig.Mailbox);
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
        return "id,subject,from,toRecipients,receivedDateTime,body,hasAttachments,internetMessageId"
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
                ContentType = NormalizePdfContentType(fileName, rawContentType, data),
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

    /// <summary>
    /// Normalizes an attachment content type to <c>application/pdf</c> when a sender
    /// mislabeled a PDF (commonly <c>application/octet-stream</c>). Keys on the
    /// <c>.pdf</c> file-name extension first — matching the MIME map in
    /// <see cref="FromMicrosoftGraphNode"/> — and falls back to sniffing the
    /// <c>%PDF-</c> magic header on the base64 content. AB#4433.
    /// </summary>
    private static string NormalizePdfContentType(string fileName, string contentType, string base64Content)
    {
        if (string.Equals(contentType, "application/pdf", StringComparison.OrdinalIgnoreCase))
        {
            return contentType;
        }

        if (fileName.EndsWith(".pdf", StringComparison.OrdinalIgnoreCase) || StartsWithPdfHeader(base64Content))
        {
            return "application/pdf";
        }

        return contentType;
    }

    /// <summary>
    /// True when the base64-encoded content begins with the <c>%PDF-</c> magic header.
    /// Only the first few base64 characters are decoded (the signature is 5 bytes).
    /// </summary>
    private static bool StartsWithPdfHeader(string base64Content)
    {
        if (string.IsNullOrEmpty(base64Content))
        {
            return false;
        }

        // 8 base64 chars decode to 6 bytes — enough for the 5-byte "%PDF-" signature.
        var prefix = base64Content.Length >= 8 ? base64Content[..8] : base64Content;
        try
        {
            var bytes = Convert.FromBase64String(prefix);
            return bytes.Length >= 5 &&
                   bytes[0] == 0x25 && bytes[1] == 0x50 && bytes[2] == 0x44 &&
                   bytes[3] == 0x46 && bytes[4] == 0x2D; // %PDF-
        }
        catch (FormatException)
        {
            return false;
        }
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
    /// The extracted parts run through <see cref="NormalizePdfContentType"/> as well
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
            var normalized = NormalizePdfContentType(name, part.ContentType.MimeType, base64);
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

    private async Task MoveMessageAsync(string accessToken, string mailbox, string messageId,
        string destinationFolderId)
    {
        using var client = CreateGraphClient(accessToken);

        var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/messages/{messageId}/move";
        var payload = JsonSerializer.Serialize(new { destinationId = destinationFolderId });
        var response = await client.PostAsync(url,
            new StringContent(payload, Encoding.UTF8, "application/json"), _cancellationTokenSource!.Token);
        response.EnsureSuccessStatusCode();
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
