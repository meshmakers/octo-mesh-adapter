using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that posts a message to a Discord channel via the Bot API.
/// </summary>
/// <param name="next">Next node in the pipeline.</param>
/// <param name="etlContext">The ETL context providing global configuration and tenant repository.</param>
/// <param name="httpClientFactory">HttpClient factory. Uses the named client "Discord".</param>
[NodeConfiguration(typeof(ToDiscordNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ToDiscordNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory) : IPipelineNode
{
    private const string DiscordApiBase = "https://discord.com/api/v10";

    /// <summary>
    /// Discord server configuration resolved from global configuration by name.
    /// Carries the bot token.
    /// </summary>
    // Internal; tests access via InternalsVisibleTo.
    // ReSharper disable once ClassNeverInstantiated.Global
    internal record DiscordConfiguration
    {
        /// <summary>Bot token used for <c>Authorization: Bot ...</c> header.</summary>
        public required string BotToken { get; init; }
    }

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ToDiscordNodeConfiguration>();

        if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, nameof(c.ServerConfiguration), c.ServerConfiguration);
        }
        var cfg = etlContext.GlobalConfiguration.GetValue<DiscordConfiguration>(c.ServerConfiguration);

        var channelId = ResolveStringValue(dataContext, c.ChannelIdPath, c.ChannelId);
        if (string.IsNullOrWhiteSpace(channelId) || !IsSnowflake(channelId))
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(
                nodeContext, new JValue(channelId ?? ""));
        }

        var content = ResolveStringValue(dataContext, c.ContentPath, c.Content);
        var embedTitle = ResolveStringValue(dataContext, c.EmbedTitlePath, c.EmbedTitle);
        var embedDescription = ResolveStringValue(dataContext, c.EmbedDescriptionPath, c.EmbedDescription);
        var embedColor = ResolveEmbedColor(dataContext, c, nodeContext);
        var attachmentRtId = ResolveStringValue(dataContext, c.AttachmentRtIdPath, c.AttachmentRtId);
        var allowedMentions = ResolveAllowedMentions(dataContext, c, nodeContext);

        var hasBody = !string.IsNullOrWhiteSpace(content)
            || !string.IsNullOrWhiteSpace(embedTitle)
            || !string.IsNullOrWhiteSpace(embedDescription)
            || !string.IsNullOrWhiteSpace(attachmentRtId);
        if (!hasBody)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(
                nodeContext, "content/embed/attachment");
        }

        var payload = new JObject();
        if (!string.IsNullOrWhiteSpace(content)) payload["content"] = content;

        if (!string.IsNullOrWhiteSpace(embedTitle) || !string.IsNullOrWhiteSpace(embedDescription)
            || embedColor.HasValue)
        {
            var embed = new JObject();
            if (!string.IsNullOrWhiteSpace(embedTitle)) embed["title"] = embedTitle;
            if (!string.IsNullOrWhiteSpace(embedDescription)) embed["description"] = embedDescription;
            if (embedColor.HasValue) embed["color"] = embedColor.Value;
            payload["embeds"] = new JArray(embed);
        }

        if (allowedMentions != null) payload["allowed_mentions"] = allowedMentions;

        var url = $"{DiscordApiBase}/channels/{channelId}/messages";

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", cfg.BotToken);

        if (!string.IsNullOrWhiteSpace(attachmentRtId))
        {
            var (stream, filename, contentType) = await DownloadAttachmentAsync(attachmentRtId!, nodeContext);
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(payload.ToString(Newtonsoft.Json.Formatting.None),
                Encoding.UTF8, "application/json"), "payload_json");
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(fileContent, "files[0]", filename);
            request.Content = multipart;
        }
        else
        {
            request.Content = new StringContent(
                payload.ToString(Newtonsoft.Json.Formatting.None), Encoding.UTF8, "application/json");
        }

        var client = httpClientFactory.CreateClient("Discord");
        client.Timeout = TimeSpan.FromSeconds(c.TimeoutSeconds);

        using var response = await client.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            var retryAfter = response.Headers.RetryAfter?.Delta?.TotalSeconds.ToString("0")
                ?? response.Headers.RetryAfter?.Date?.ToString("o");
            throw MeshAdapterPipelineExecutionException.DiscordApiFailed(
                nodeContext, (int)response.StatusCode, body, channelId, retryAfter);
        }

        if (!string.IsNullOrWhiteSpace(c.TargetPath))
        {
            dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode, JToken.Parse(body));
        }

        await next(dataContext, nodeContext);
    }

    private static string? ResolveStringValue(IDataContext dc, string? path, string? literal)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var resolved = dc.GetSimpleValueByPath<string>(path);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        }
        return literal;
    }

    private static int? ResolveEmbedColor(IDataContext dc, ToDiscordNodeConfiguration c,
        INodeContext nodeContext)
    {
        if (!string.IsNullOrWhiteSpace(c.EmbedColorPath))
        {
            var raw = dc.GetSimpleValueByPath<string>(c.EmbedColorPath);
            if (string.IsNullOrWhiteSpace(raw)) return c.EmbedColor;
            var trimmed = raw.Trim();
            var hex = trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
                ? trimmed[2..]
                : (trimmed.StartsWith('#') ? trimmed[1..] : null);
            if (hex != null)
            {
                if (int.TryParse(hex, System.Globalization.NumberStyles.HexNumber,
                        System.Globalization.CultureInfo.InvariantCulture, out var hv))
                {
                    return hv;
                }
                throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, new JValue(raw));
            }
            if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var dv))
            {
                return dv;
            }
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, new JValue(raw));
        }
        return c.EmbedColor;
    }

    private static JToken? ResolveAllowedMentions(IDataContext dc, ToDiscordNodeConfiguration c,
        INodeContext nodeContext)
    {
        switch (c.MentionPolicy)
        {
            case MentionPolicy.All:
                return null; // omit allowed_mentions; Discord's permissive default
            case MentionPolicy.None:
                return new JObject { ["parse"] = new JArray() };
            case MentionPolicy.Users:
                return new JObject { ["parse"] = new JArray("users") };
            case MentionPolicy.Roles:
                return new JObject { ["parse"] = new JArray("roles") };
            case MentionPolicy.UsersAndRoles:
                return new JObject { ["parse"] = new JArray("users", "roles") };
            case MentionPolicy.Custom:
                if (string.IsNullOrWhiteSpace(c.AllowedMentionsPath))
                {
                    throw MeshAdapterPipelineExecutionException.InputValueNull(
                        nodeContext, nameof(c.AllowedMentionsPath));
                }
                var resolved = dc.GetComplexObjectByPath<JToken>(c.AllowedMentionsPath);
                if (resolved == null)
                {
                    throw MeshAdapterPipelineExecutionException.InputValueNull(
                        nodeContext, c.AllowedMentionsPath);
                }
                return resolved;
            default:
                throw MeshAdapterPipelineExecutionException.InvalidValue(
                    nodeContext, new JValue(c.MentionPolicy.ToString()));
        }
    }

    private static bool IsSnowflake(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9') return false;
        }
        return true;
    }

    private async Task<(Stream Stream, string Filename, string ContentType)> DownloadAttachmentAsync(
        string rtId, INodeContext nodeContext)
    {
        var tenantRepository = etlContext.TenantRepository;
        using var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        session.StartTransaction();
        var handler = await tenantRepository.DownloadLargeBinaryAsync(
            session, OctoObjectId.Parse(rtId), CancellationToken.None);
        await session.CommitTransactionAsync().ConfigureAwait(false);

        if (handler == null)
        {
            throw MeshAdapterPipelineExecutionException.BinaryNotFound(nodeContext, rtId);
        }

        return (handler.Stream, handler.Filename, handler.ContentType);
    }
}
