using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromMicrosoftGraphEmailNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromMicrosoftGraphEmailNode(
    ILogger<FromMicrosoftGraphEmailNode> logger,
    IHttpClientFactory httpClientFactory)
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

        _cancellationTokenSource = new CancellationTokenSource();
        _pollingTask = Task.Run(
            async () => await PollForMessagesAsync(context, graphConfig, c),
            _cancellationTokenSource.Token);

        return Task.CompletedTask;
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

                    var emailData = await BuildEmailDataAsync(accessToken, nodeConfig.Mailbox, messageId, message);

                    var batch = new EmailBatch
                    {
                        Emails = [emailData],
                        Count = 1,
                        ProcessedAt = DateTime.UtcNow
                    };

                    try
                    {
                        // One pipeline run per message so the success/failure of a run maps
                        // 1:1 to the move decision for exactly that message.
                        await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), batch);

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
                await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
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

    private async Task<List<JsonElement>> GetMessagesAsync(string accessToken,
        FromMicrosoftGraphEmailNodeConfiguration config, string folderId)
    {
        using var client = CreateGraphClient(accessToken);

        var url =
            $"{GraphBaseUrl}/users/{Uri.EscapeDataString(config.Mailbox)}/mailFolders/{folderId}/messages" +
            $"?$top={config.MaxMessagesPerPoll}&$orderby=receivedDateTime asc" +
            "&$select=id,subject,from,toRecipients,receivedDateTime,body,hasAttachments,internetMessageId";

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

    private async Task<EmailData> BuildEmailDataAsync(string accessToken, string mailbox, string messageId,
        JsonElement message)
    {
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

        var hasAttachments = message.TryGetProperty("hasAttachments", out var ha) && ha.GetBoolean();
        var attachments = hasAttachments
            ? await GetAttachmentsAsync(accessToken, mailbox, messageId)
            : [];

        return new EmailData
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
            attachments.Add(new AttachmentData
            {
                FileName = att.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown",
                ContentType = att.TryGetProperty("contentType", out var ct)
                    ? ct.GetString() ?? "application/octet-stream"
                    : "application/octet-stream",
                Data = data,
                Length = att.TryGetProperty("size", out var size) && size.TryGetInt64(out var sizeValue)
                    ? sizeValue
                    : (long)(data.Length * 0.75)
            });
        }

        return attachments;
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
