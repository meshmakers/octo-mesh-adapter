using System.Net.Http.Headers;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

[NodeConfiguration(typeof(FromMicrosoftGraphNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromMicrosoftGraphNode(ILogger<FromMicrosoftGraphNode> logger, IHttpClientFactory httpClientFactory)
    : ITriggerPipelineNode
{
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
        var c = context.NodeContext.GetNodeConfiguration<FromMicrosoftGraphNodeConfiguration>();

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
                logger.LogWarning("Graph polling task did not complete within timeout");
            }
        }

        _cancellationTokenSource?.Dispose();
    }

    private async Task PollForMessagesAsync(ITriggerContext context, GraphConfiguration graphConfig,
        FromMicrosoftGraphNodeConfiguration nodeConfig)
    {
        string? deltaLink = null;
        var processedMessageIds = new HashSet<string>();

        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                var accessToken = await GetAccessTokenAsync(graphConfig);
                var (messages, nextDeltaLink) =
                    await GetChannelMessagesAsync(accessToken, nodeConfig, deltaLink);
                deltaLink = nextDeltaLink;

                var newMessages = new List<EmailData>();
                foreach (var message in messages)
                {
                    var messageId = message.GetProperty("id").GetString();
                    if (messageId == null || processedMessageIds.Contains(messageId))
                        continue;

                    // Skip system/control messages
                    if (message.TryGetProperty("messageType", out var msgType) &&
                        msgType.GetString() != "message")
                        continue;

                    // Apply sender filter
                    var senderName = GetSenderName(message);
                    if (!string.IsNullOrWhiteSpace(nodeConfig.SenderFilter) &&
                        (senderName == null || !senderName.Contains(nodeConfig.SenderFilter,
                            StringComparison.OrdinalIgnoreCase)))
                        continue;

                    // Check for attachments
                    var attachments = await GetAttachmentsAsync(accessToken, nodeConfig, messageId, message);
                    if (attachments.Count == 0)
                        continue;

                    var subject = message.TryGetProperty("subject", out var subj) ? subj.GetString() : null;
                    var body = GetBodyContent(message);
                    var createdDateTime = message.TryGetProperty("createdDateTime", out var dt)
                        ? dt.GetDateTime()
                        : DateTime.UtcNow;

                    newMessages.Add(new EmailData
                    {
                        Subject = subject ?? senderName ?? "Teams message",
                        From = senderName,
                        Date = createdDateTime,
                        Body = body,
                        TextBody = body,
                        MessageId = messageId,
                        Attachments = attachments
                    });

                    processedMessageIds.Add(messageId);
                }

                if (newMessages.Count > 0)
                {
                    var batch = new EmailBatch
                    {
                        Emails = newMessages,
                        Count = newMessages.Count,
                        ProcessedAt = DateTime.UtcNow
                    };

                    await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), batch);
                    logger.LogInformation("Processed {Count} new Teams channel messages with attachments",
                        newMessages.Count);
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
                logger.LogError(ex, "Error while polling Microsoft Graph for Teams messages");
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

    private async Task<(List<JsonElement> Messages, string? DeltaLink)> GetChannelMessagesAsync(
        string accessToken, FromMicrosoftGraphNodeConfiguration config, string? deltaLink)
    {
        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        var url = deltaLink ??
                  $"https://graph.microsoft.com/v1.0/teams/{config.TeamId}/channels/{config.ChannelId}/messages/delta";

        var allMessages = new List<JsonElement>();
        string? nextDeltaLink = null;

        while (url != null)
        {
            var response = await client.GetAsync(url, _cancellationTokenSource!.Token);
            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync(_cancellationTokenSource.Token);
            var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (root.TryGetProperty("value", out var values))
            {
                foreach (var msg in values.EnumerateArray())
                {
                    allMessages.Add(msg.Clone());
                }
            }

            // Follow @odata.nextLink for pagination
            if (root.TryGetProperty("@odata.nextLink", out var nextLink))
            {
                url = nextLink.GetString();
            }
            else
            {
                url = null;
            }

            // Capture @odata.deltaLink for next poll
            if (root.TryGetProperty("@odata.deltaLink", out var delta))
            {
                nextDeltaLink = delta.GetString();
            }
        }

        return (allMessages, nextDeltaLink);
    }

    private async Task<List<AttachmentData>> GetAttachmentsAsync(string accessToken,
        FromMicrosoftGraphNodeConfiguration config, string messageId, JsonElement message)
    {
        var attachments = new List<AttachmentData>();

        if (!message.TryGetProperty("attachments", out var attachmentArray))
            return attachments;

        using var client = httpClientFactory.CreateClient();
        client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);

        foreach (var att in attachmentArray.EnumerateArray())
        {
            var name = att.TryGetProperty("name", out var n) ? n.GetString() ?? "unknown" : "unknown";
            var attContentType = att.TryGetProperty("contentType", out var ct)
                ? ct.GetString() ?? ""
                : "";

            if (!att.TryGetProperty("contentUrl", out var contentUrlProp) ||
                contentUrlProp.GetString() == null)
                continue;

            var contentUrl = contentUrlProp.GetString()!;

            try
            {
                byte[] bytes;

                if (attContentType == "reference" && contentUrl.Contains(".sharepoint.com",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // File attachment stored in SharePoint — download via Graph API sharing URL
                    // Encode the SharePoint URL as a sharing token: base64url("u!" + url)
                    var shareToken = "u!" + Convert.ToBase64String(
                            System.Text.Encoding.UTF8.GetBytes(contentUrl))
                        .TrimEnd('=').Replace('/', '_').Replace('+', '-');

                    var graphUrl =
                        $"https://graph.microsoft.com/v1.0/shares/{shareToken}/driveItem/content";

                    bytes = await client.GetByteArrayAsync(graphUrl, _cancellationTokenSource!.Token);
                }
                else
                {
                    // Hosted content (inline images etc.) — download via hostedContents endpoint
                    if (att.TryGetProperty("id", out var attId) && attId.GetString() != null)
                    {
                        var hostedUrl =
                            $"https://graph.microsoft.com/v1.0/teams/{config.TeamId}/channels/{config.ChannelId}/messages/{messageId}/hostedContents/{attId.GetString()}/$value";
                        bytes = await client.GetByteArrayAsync(hostedUrl, _cancellationTokenSource!.Token);
                    }
                    else
                    {
                        continue;
                    }
                }

                // Determine MIME type from file extension if contentType is "reference"
                var mimeType = attContentType == "reference"
                    ? GetMimeTypeFromFileName(name)
                    : attContentType;

                attachments.Add(new AttachmentData
                {
                    FileName = name,
                    ContentType = mimeType,
                    Data = Convert.ToBase64String(bytes),
                    Length = bytes.Length
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "Failed to download attachment '{Name}' from message {MessageId}",
                    name, messageId);
            }
        }

        return attachments;
    }

    private static string GetMimeTypeFromFileName(string fileName)
    {
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        return ext switch
        {
            ".pdf" => "application/pdf",
            ".png" => "image/png",
            ".jpg" or ".jpeg" => "image/jpeg",
            ".doc" => "application/msword",
            ".docx" => "application/vnd.openxmlformats-officedocument.wordprocessingml.document",
            ".xls" => "application/vnd.ms-excel",
            ".xlsx" => "application/vnd.openxmlformats-officedocument.spreadsheetml.sheet",
            ".csv" => "text/csv",
            ".txt" => "text/plain",
            ".xml" => "application/xml",
            _ => "application/octet-stream"
        };
    }

    private static string? GetSenderName(JsonElement message)
    {
        if (message.TryGetProperty("from", out var from) &&
            from.TryGetProperty("user", out var user) &&
            user.TryGetProperty("displayName", out var displayName))
        {
            return displayName.GetString();
        }

        return null;
    }

    private static string? GetBodyContent(JsonElement message)
    {
        if (message.TryGetProperty("body", out var body) &&
            body.TryGetProperty("content", out var content))
        {
            return content.GetString();
        }

        return null;
    }
}
