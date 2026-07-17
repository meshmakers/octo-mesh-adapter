using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Microsoft.Extensions.Logging;
using HttpMethod = Meshmakers.Octo.MeshAdapter.Nodes.Trigger.HttpMethod;
using HttpRequestOptions = Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests.HttpRequestOptions;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Trigger node that hosts the Microsoft Bot Framework messaging endpoint
/// (<c>POST /{tenant}{Route}</c>) and executes the pipeline for each inbound Teams activity.
/// File attachments are downloaded and normalised into the same <c>EmailData</c>/
/// <c>AttachmentData</c> shape used by <c>FromEmail@1</c>/<c>FromMicrosoftGraph@1</c>, so the
/// downstream document/Q&amp;A pipeline is channel-agnostic. Conversation routing metadata
/// (serviceUrl/conversationId/activityId) is carried at <c>$.Conversation</c> for the outbound
/// <c>TeamsBotReply@1</c> node.
/// </summary>
[NodeConfiguration(typeof(FromTeamsBotNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromTeamsBotNode(
    ILogger<FromTeamsBotNode> logger,
    IHttpRequestService httpRequestService,
    IHttpClientFactory httpClientFactory) : ITriggerPipelineNode
{
    private HttpRouteHandle? _routeHandle;

    // ReSharper disable once ClassNeverInstantiated.Local
    private record GraphBotConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string AzureTenantId { get; init; }
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    public Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromTeamsBotNodeConfiguration>();

        if (!context.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext, nameof(c.ServerConfiguration), c.ServerConfiguration);
        }

        var cfg = context.GlobalConfiguration.GetValue<GraphBotConfiguration>(c.ServerConfiguration);
        var expectedAudience = string.IsNullOrWhiteSpace(c.BotAppId) ? cfg.ClientId : c.BotAppId!;

        var requestOptions = new HttpRequestOptions(c.Route, HttpMethod.Post, async input =>
        {
            try
            {
                return await HandleActivityAsync(context, c, cfg, expectedAudience, input);
            }
            catch (Exception e)
            {
                // Never surface a 500 to Bot Framework (it would retry the same activity). Log and
                // acknowledge with an empty 200; the reply, if any, is sent via TeamsBotReply.
                logger.LogError(e, "FromTeamsBot: failed to process inbound activity");
                return null;
            }
        });
        _routeHandle = httpRequestService.CreateRoute(requestOptions);

        logger.LogInformation("FromTeamsBot: listening on {Route}", c.Route);
        return Task.CompletedTask;
    }

    public Task StopAsync(ITriggerContext context)
    {
        _routeHandle?.Dispose();
        return Task.CompletedTask;
    }

    private async Task<JsonNode?> HandleActivityAsync(ITriggerContext context,
        FromTeamsBotNodeConfiguration c, GraphBotConfiguration cfg, string expectedAudience,
        JsonNode input)
    {
        // Bot Framework sends Content-Type "application/json; charset=utf-8"; the adapter's
        // HTTP host only parses a body into a JsonObject on an exact "application/json" match,
        // otherwise it hands it over as a raw JSON string. Handle both.
        var activity = ParseActivity(input["body"]);
        if (activity is null)
        {
            return null;
        }

        // Only act on user messages; ignore conversationUpdate/typing/etc. (acknowledge with 200).
        var activityType = activity["type"]?.GetValue<string>();
        if (!string.Equals(activityType, "message", StringComparison.OrdinalIgnoreCase))
        {
            return null;
        }

        if (c.ValidateInboundToken)
        {
            var authHeader = input["headers"]?["Authorization"]?.GetValue<string>();
            if (!IsInboundTokenAcceptable(authHeader, expectedAudience))
            {
                logger.LogWarning("FromTeamsBot: rejected inbound activity (token check failed)");
                return null;
            }
        }

        var text = StripHtml(activity["text"]?.GetValue<string>());

        var from = activity["from"] as JsonObject;
        var fromId = from?["id"]?.GetValue<string>();
        var fromName = from?["name"]?.GetValue<string>();
        var fromAad = from?["aadObjectId"]?.GetValue<string>();

        var serviceUrl = activity["serviceUrl"]?.GetValue<string>();
        var conversationId = (activity["conversation"] as JsonObject)?["id"]?.GetValue<string>();
        var activityId = activity["id"]?.GetValue<string>();

        var attachments = await DownloadAttachmentsAsync(activity["attachments"] as JsonArray, cfg);

        var email = new EmailData
        {
            Subject = "Teams message",
            From = fromName,
            FromAddress = string.IsNullOrWhiteSpace(fromAad) ? fromId : fromAad,
            Date = DateTime.UtcNow,
            Body = text,
            TextBody = text,
            MessageId = activityId,
            Attachments = attachments
        };

        var batch = new TeamsActivityBatch
        {
            Emails = new List<EmailData> { email },
            Conversation = new TeamsConversation
            {
                ServiceUrl = serviceUrl,
                ConversationId = conversationId,
                ActivityId = activityId,
                FromId = fromId,
                FromName = fromName,
                FromAadObjectId = fromAad
            },
            Count = 1,
            ProcessedAt = DateTime.UtcNow
        };

        await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow), batch);
        logger.LogInformation(
            "FromTeamsBot: processed activity from {From} with {AttachmentCount} attachment(s)",
            fromName ?? fromId ?? "unknown", attachments.Count);

        // Empty 200 acknowledgement; any reply is delivered out-of-band by TeamsBotReply.
        return null;
    }

    /// <summary>
    /// Resolves the inbound activity whether the HTTP host delivered it as a parsed
    /// <see cref="JsonObject"/> (exact <c>application/json</c>) or as a raw JSON string
    /// (<c>application/json; charset=utf-8</c>, as Bot Framework sends it).
    /// </summary>
    private static JsonObject? ParseActivity(JsonNode? body)
    {
        switch (body)
        {
            case JsonObject o:
                return o;
            case JsonValue v when v.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s):
                try { return JsonNode.Parse(s) as JsonObject; }
                catch { return null; }
            default:
                return null;
        }
    }

    private async Task<List<AttachmentData>> DownloadAttachmentsAsync(JsonArray? attachmentArray,
        GraphBotConfiguration cfg)
    {
        var attachments = new List<AttachmentData>();
        if (attachmentArray == null || attachmentArray.Count == 0)
        {
            return attachments;
        }

        string? graphToken = null;
        using var client = httpClientFactory.CreateClient("TeamsBot");

        foreach (var attNode in attachmentArray)
        {
            if (attNode is not JsonObject att)
            {
                continue;
            }

            var name = att["name"]?.GetValue<string>() ?? "attachment";
            var contentType = att["contentType"]?.GetValue<string>() ?? "";

            try
            {
                byte[]? bytes = null;

                if (string.Equals(contentType,
                        "application/vnd.microsoft.teams.file.download.info",
                        StringComparison.OrdinalIgnoreCase))
                {
                    // 1:1 / group-chat file upload — pre-authenticated download URL.
                    var downloadUrl = (att["content"] as JsonObject)?["downloadUrl"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(downloadUrl))
                    {
                        bytes = await client.GetByteArrayAsync(downloadUrl);
                    }
                }
                else if (string.Equals(contentType, "reference", StringComparison.OrdinalIgnoreCase))
                {
                    // Channel file — stored in SharePoint, downloaded via Microsoft Graph.
                    var contentUrl = att["contentUrl"]?.GetValue<string>();
                    if (!string.IsNullOrWhiteSpace(contentUrl) &&
                        contentUrl!.Contains(".sharepoint.com", StringComparison.OrdinalIgnoreCase))
                    {
                        graphToken ??= await GetGraphTokenAsync(cfg);
                        var shareToken = "u!" + Convert.ToBase64String(Encoding.UTF8.GetBytes(contentUrl))
                            .TrimEnd('=').Replace('/', '_').Replace('+', '-');
                        var graphUrl = $"https://graph.microsoft.com/v1.0/shares/{shareToken}/driveItem/content";

                        using var req = new HttpRequestMessage(System.Net.Http.HttpMethod.Get, graphUrl);
                        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", graphToken);
                        using var resp = await client.SendAsync(req);
                        resp.EnsureSuccessStatusCode();
                        bytes = await resp.Content.ReadAsByteArrayAsync();
                    }
                }

                if (bytes == null)
                {
                    continue;
                }

                var mime = string.Equals(contentType, "reference", StringComparison.OrdinalIgnoreCase)
                    ? GetMimeTypeFromFileName(name)
                    : GetMimeTypeFromFileName(name);

                attachments.Add(new AttachmentData
                {
                    FileName = name,
                    ContentType = mime,
                    Data = Convert.ToBase64String(bytes),
                    Length = bytes.Length
                });
            }
            catch (Exception ex)
            {
                logger.LogWarning(ex, "FromTeamsBot: failed to download attachment '{Name}'", name);
            }
        }

        return attachments;
    }

    private async Task<string> GetGraphTokenAsync(GraphBotConfiguration config)
    {
        using var client = httpClientFactory.CreateClient("TeamsBot");
        var tokenUrl = $"https://login.microsoftonline.com/{config.AzureTenantId}/oauth2/v2.0/token";

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["client_id"] = config.ClientId,
            ["client_secret"] = config.ClientSecret,
            ["scope"] = "https://graph.microsoft.com/.default",
            ["grant_type"] = "client_credentials"
        });

        using var response = await client.PostAsync(tokenUrl, content);
        response.EnsureSuccessStatusCode();
        var json = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// Best-effort inbound-token check: verifies audience and expiry from the JWT payload.
    /// NOTE: does NOT verify the cryptographic signature (see
    /// <see cref="FromTeamsBotNodeConfiguration.ValidateInboundToken"/> remarks). Harden before
    /// exposing publicly.
    /// </summary>
    private bool IsInboundTokenAcceptable(string? authHeader, string expectedAudience)
    {
        if (string.IsNullOrWhiteSpace(authHeader) ||
            !authHeader.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var token = authHeader["Bearer ".Length..].Trim();
        var parts = token.Split('.');
        if (parts.Length != 3)
        {
            return false;
        }

        try
        {
            var payloadJson = Encoding.UTF8.GetString(Base64UrlDecode(parts[1]));
            using var doc = JsonDocument.Parse(payloadJson);
            var root = doc.RootElement;

            var aud = root.TryGetProperty("aud", out var audEl) ? audEl.GetString() : null;
            if (!string.Equals(aud, expectedAudience, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            if (root.TryGetProperty("exp", out var expEl) && expEl.TryGetInt64(out var exp))
            {
                var expiresAt = DateTimeOffset.FromUnixTimeSeconds(exp);
                if (expiresAt < DateTimeOffset.UtcNow.AddMinutes(-5))
                {
                    return false;
                }
            }

            return true;
        }
        catch
        {
            return false;
        }
    }

    private static byte[] Base64UrlDecode(string input)
    {
        var s = input.Replace('-', '+').Replace('_', '/');
        switch (s.Length % 4)
        {
            case 2: s += "=="; break;
            case 3: s += "="; break;
        }
        return Convert.FromBase64String(s);
    }

    private static string? StripHtml(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return html;
        }
        var text = Regex.Replace(html, "<[^>]+>", string.Empty);
        return System.Net.WebUtility.HtmlDecode(text).Trim();
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
}

/// <summary>
/// Bot Framework conversation routing metadata carried at <c>$.Conversation</c> so the
/// outbound <c>TeamsBotReply@1</c> node can address the reply.
/// </summary>
public sealed class TeamsConversation
{
    /// <summary>Bot Framework <c>serviceUrl</c> to send replies to.</summary>
    public string? ServiceUrl { get; set; }
    /// <summary>Id of the conversation to reply into.</summary>
    public string? ConversationId { get; set; }
    /// <summary>Id of the inbound activity (used for threaded replies).</summary>
    public string? ActivityId { get; set; }
    /// <summary>Channel account id of the sender.</summary>
    public string? FromId { get; set; }
    /// <summary>Display name of the sender.</summary>
    public string? FromName { get; set; }
    /// <summary>Azure AD object id of the sender (stable per-user identity), when available.</summary>
    public string? FromAadObjectId { get; set; }
}

/// <summary>
/// Single-activity payload emitted by <c>FromTeamsBot@1</c>. Reuses the <c>EmailData</c> shape
/// (at <c>$.Emails</c>) so the document/Q&amp;A pipeline is identical to the email assistant,
/// plus conversation routing at <c>$.Conversation</c>.
/// </summary>
public sealed class TeamsActivityBatch
{
    /// <summary>The inbound message(s) in the <c>EmailData</c> shape (single item per activity).</summary>
    public List<EmailData> Emails { get; set; } = new();
    /// <summary>Conversation routing metadata for the outbound reply.</summary>
    public TeamsConversation Conversation { get; set; } = new();
    /// <summary>Number of messages in this batch (always 1 for a single activity).</summary>
    public int Count { get; set; }
    /// <summary>UTC timestamp when the activity was processed.</summary>
    public DateTime ProcessedAt { get; set; }
}
