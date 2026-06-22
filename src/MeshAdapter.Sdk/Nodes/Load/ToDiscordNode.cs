using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

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

    private static readonly RtCkId<CkTypeId> FileSystemItemCkTypeId =
        new("System.Reporting/FileSystemItem");

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
                nodeContext, channelId ?? "");
        }

        var content = ResolveStringValue(dataContext, c.ContentPath, c.Content);
        var embedTitle = ResolveStringValue(dataContext, c.EmbedTitlePath, c.EmbedTitle);
        var embedDescription = ResolveStringValue(dataContext, c.EmbedDescriptionPath, c.EmbedDescription);
        var embedColor = ResolveEmbedColor(dataContext, c, nodeContext);
        var fileSystemItemRtId = ResolveStringValue(
            dataContext, c.AttachmentFileSystemItemRtIdPath, c.AttachmentFileSystemItemRtId);
        var filenameOverride = ResolveStringValue(
            dataContext, c.AttachmentFilenamePath, c.AttachmentFilename);
        var allowedMentions = ResolveAllowedMentions(dataContext, c, nodeContext);

        var hasBody = !string.IsNullOrWhiteSpace(content)
            || !string.IsNullOrWhiteSpace(embedTitle)
            || !string.IsNullOrWhiteSpace(embedDescription)
            || !string.IsNullOrWhiteSpace(fileSystemItemRtId);
        if (!hasBody)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(
                nodeContext, "content/embed/attachment");
        }

        DiscordEmbed? embed = null;
        if (!string.IsNullOrWhiteSpace(embedTitle) || !string.IsNullOrWhiteSpace(embedDescription)
            || embedColor.HasValue)
        {
            embed = new DiscordEmbed(
                string.IsNullOrWhiteSpace(embedTitle) ? null : embedTitle,
                string.IsNullOrWhiteSpace(embedDescription) ? null : embedDescription,
                embedColor);
        }

        var payload = new DiscordPayload(
            string.IsNullOrWhiteSpace(content) ? null : content,
            embed == null ? null : new[] { embed },
            allowedMentions);

        // Discord is an external API; the body must stay compact (SystemTextJsonOptions.Default
        // is not indented) and byte-stable. Omitted optional keys come from the property-level
        // WhenWritingNull overrides (the pipeline default otherwise preserves nulls).
        var payloadJson = JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default);

        var url = $"{DiscordApiBase}/channels/{channelId}/messages";

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.ToDiscord, new
            {
                channelId,
                endpoint = url,
                hasContent = !string.IsNullOrWhiteSpace(content),
                contentLength = content?.Length ?? 0,
                embedTitle,
                embedDescription,
                embedColor,
                hasAttachment = !string.IsNullOrWhiteSpace(fileSystemItemRtId),
                attachmentFileSystemItemRtId = fileSystemItemRtId,
                payloadJson
            });
            await next(dataContext, nodeContext);
            return;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bot", cfg.BotToken);

        if (!string.IsNullOrWhiteSpace(fileSystemItemRtId))
        {
            var (stream, filename, contentType) =
                await DownloadAttachmentAsync(fileSystemItemRtId!, filenameOverride, nodeContext);
            var multipart = new MultipartFormDataContent();
            multipart.Add(new StringContent(payloadJson,
                Encoding.UTF8, "application/json"), "payload_json");
            var fileContent = new StreamContent(stream);
            fileContent.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            multipart.Add(fileContent, "files[0]", filename);
            request.Content = multipart;
        }
        else
        {
            request.Content = new StringContent(
                payloadJson, Encoding.UTF8, "application/json");
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
            dataContext.Set(c.TargetPath, JsonNode.Parse(body), c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private static string? ResolveStringValue(IDataContext dc, string? path, string? literal)
    {
        if (!string.IsNullOrWhiteSpace(path))
        {
            var resolved = dc.Get<string>(path);
            if (!string.IsNullOrWhiteSpace(resolved)) return resolved;
        }
        return literal;
    }

    private static int? ResolveEmbedColor(IDataContext dc, ToDiscordNodeConfiguration c,
        INodeContext nodeContext)
    {
        if (!string.IsNullOrWhiteSpace(c.EmbedColorPath))
        {
            var raw = dc.Get<string>(c.EmbedColorPath);
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
                throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, raw);
            }
            if (int.TryParse(trimmed, System.Globalization.NumberStyles.Integer,
                    System.Globalization.CultureInfo.InvariantCulture, out var dv))
            {
                return dv;
            }
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, raw);
        }
        return c.EmbedColor;
    }

    private static object? ResolveAllowedMentions(IDataContext dc, ToDiscordNodeConfiguration c,
        INodeContext nodeContext)
    {
        switch (c.MentionPolicy)
        {
            case MentionPolicy.All:
                return null; // omit allowed_mentions; Discord's permissive default
            case MentionPolicy.None:
                return new DiscordAllowedMentions(Array.Empty<string>());
            case MentionPolicy.Users:
                return new DiscordAllowedMentions(new[] { "users" });
            case MentionPolicy.Roles:
                return new DiscordAllowedMentions(new[] { "roles" });
            case MentionPolicy.UsersAndRoles:
                return new DiscordAllowedMentions(new[] { "users", "roles" });
            case MentionPolicy.Custom:
                if (string.IsNullOrWhiteSpace(c.AllowedMentionsPath))
                {
                    throw MeshAdapterPipelineExecutionException.InputValueNull(
                        nodeContext, nameof(c.AllowedMentionsPath));
                }
                // Custom mentions are arbitrary JSON supplied by the pipeline; read as object?
                // (objects/arrays materialize to JsonElement) so it re-serializes byte-identically.
                var resolved = dc.Get<object?>(c.AllowedMentionsPath);
                if (resolved == null)
                {
                    throw MeshAdapterPipelineExecutionException.InputValueNull(
                        nodeContext, c.AllowedMentionsPath);
                }
                return resolved;
            default:
                throw MeshAdapterPipelineExecutionException.InvalidValue(
                    nodeContext, c.MentionPolicy.ToString());
        }
    }

    /// <summary>
    /// Typed Discord webhook message payload. Each optional key is omitted when null via the
    /// property-level WhenWritingNull override (the pipeline default preserves nulls); key order
    /// (content, embeds, allowed_mentions) reproduces the former hand-built JsonObject.
    /// </summary>
    internal sealed record DiscordPayload(
        [property: JsonPropertyName("content")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Content,
        [property: JsonPropertyName("embeds")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<DiscordEmbed>? Embeds,
        [property: JsonPropertyName("allowed_mentions")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        object? AllowedMentions);

    internal sealed record DiscordEmbed(
        [property: JsonPropertyName("title")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Title,
        [property: JsonPropertyName("description")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? Description,
        [property: JsonPropertyName("color")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        int? Color);

    internal sealed record DiscordAllowedMentions(
        [property: JsonPropertyName("parse")] IReadOnlyList<string> Parse);

    private static bool IsSnowflake(string s)
    {
        if (string.IsNullOrEmpty(s)) return false;
        foreach (var ch in s)
        {
            if (ch < '0' || ch > '9') return false;
        }
        return true;
    }

    /// <summary>
    /// Resolves a FileSystemItem's bound binary and chooses the attachment filename.
    /// Precedence: <paramref name="filenameOverride"/> ▶ FileSystemItem.Name ▶ Content.Filename.
    /// </summary>
    private async Task<(Stream Stream, string Filename, string ContentType)> DownloadAttachmentAsync(
        string fileSystemItemRtId, string? filenameOverride, INodeContext nodeContext)
    {
        var tenantRepository = etlContext.TenantRepository;

        EntityBinaryInfo? content;
        string? entityName;
        using (var session = await tenantRepository.GetSessionAsync().ConfigureAwait(false))
        {
            session.StartTransaction();
            var result = await tenantRepository.GetRtEntitiesByIdAsync(
                session,
                FileSystemItemCkTypeId,
                new List<OctoObjectId> { OctoObjectId.Parse(fileSystemItemRtId) },
                RtEntityQueryOptions.Create(),
                skip: 0,
                take: 1);
            await session.CommitTransactionAsync().ConfigureAwait(false);

            var fsItem = result.Items.FirstOrDefault();
            if (fsItem == null)
            {
                throw MeshAdapterPipelineExecutionException.FileSystemItemNotFound(
                    nodeContext, fileSystemItemRtId);
            }

            content = fsItem.GetAttributeValueOrDefault("Content") as EntityBinaryInfo;
            entityName = fsItem.GetAttributeValueOrDefault("Name") as string;
        }

        if (content?.BinaryId == null)
        {
            throw MeshAdapterPipelineExecutionException.FileSystemItemMissingBinary(
                nodeContext, fileSystemItemRtId);
        }

        var filename = FirstNonBlank(filenameOverride, entityName, content.Filename)
                       ?? "attachment";

        using var downloadSession = await tenantRepository.GetSessionAsync().ConfigureAwait(false);
        downloadSession.StartTransaction();
        var handler = await tenantRepository.DownloadLargeBinaryAsync(
            downloadSession, content.BinaryId.Value, CancellationToken.None);
        await downloadSession.CommitTransactionAsync().ConfigureAwait(false);

        if (handler == null)
        {
            throw MeshAdapterPipelineExecutionException.BinaryNotFound(
                nodeContext, content.BinaryId.Value.ToString()!);
        }

        var contentType = !string.IsNullOrWhiteSpace(handler.ContentType)
            ? handler.ContentType
            : (content.ContentType ?? "application/octet-stream");

        return (handler.Stream, filename, contentType);
    }

    private static string? FirstNonBlank(params string?[] candidates) =>
        candidates.FirstOrDefault(c => !string.IsNullOrWhiteSpace(c));
}
