using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that posts a reply into an ongoing Microsoft Teams conversation via the
/// Bot Framework REST API (<c>POST {serviceUrl}/v3/conversations/{conversationId}/activities</c>).
/// Outbound counterpart of <c>FromTeamsBot@1</c>. The bot credentials come from a
/// <c>MicrosoftGraphConfiguration</c> resolved by name; the conversation routing values are
/// read from the data context (captured by the inbound trigger).
/// </summary>
/// <param name="next">Next node in the pipeline.</param>
/// <param name="etlContext">The ETL context providing global configuration.</param>
/// <param name="httpClientFactory">HttpClient factory. Uses the named client "TeamsBot".</param>
[NodeConfiguration(typeof(TeamsBotReplyNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class TeamsBotReplyNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory) : IPipelineNode
{
    // Multi-tenant bots authenticate against the botframework.com authority; single-tenant
    // bots (the only option when the AAD tenant blocks multi-tenant app registrations)
    // authenticate against their own tenant authority. Both use the same scope.
    private const string MultiTenantTokenUrl =
        "https://login.microsoftonline.com/botframework.com/oauth2/v2.0/token";

    private const string BotFrameworkScope = "https://api.botframework.com/.default";

    /// <summary>
    /// Bot credentials resolved from the MicrosoftGraphConfiguration by name. The App
    /// Registration's <c>ClientId</c>/<c>ClientSecret</c> double as the bot App ID/secret;
    /// <c>AzureTenantId</c>, when set, selects the single-tenant token authority.
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Local
    private record BotConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Local
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        public string? AzureTenantId { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Local
    }

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<TeamsBotReplyNodeConfiguration>();

        if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, nameof(c.ServerConfiguration), c.ServerConfiguration);
        }
        var cfg = etlContext.GlobalConfiguration.GetValue<BotConfiguration>(c.ServerConfiguration);

        var serviceUrl = dataContext.Get<string>(c.ServiceUrlPath);
        var conversationId = dataContext.Get<string>(c.ConversationIdPath);
        var replyToId = string.IsNullOrWhiteSpace(c.ReplyToActivityIdPath)
            ? null
            : dataContext.Get<string>(c.ReplyToActivityIdPath);
        var text = ResolveStringValue(dataContext, c.MessageBodyPath, c.MessageBody);

        if (string.IsNullOrWhiteSpace(serviceUrl) || string.IsNullOrWhiteSpace(conversationId))
        {
            HandleFailure(c, nodeContext,
                $"TeamsBotReply: serviceUrl or conversationId is not set " +
                $"(ServiceUrlPath={c.ServiceUrlPath}, ConversationIdPath={c.ConversationIdPath})");
            await next(dataContext, nodeContext);
            return;
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            // Nothing to say — skip silently rather than sending an empty activity.
            nodeContext.Warning("TeamsBotReply: message body is empty, skipping reply");
            await next(dataContext, nodeContext);
            return;
        }

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.Info("TeamsBotReply (dry-run): would reply to conversation {0} at {1}",
                conversationId, serviceUrl);
            await next(dataContext, nodeContext);
            return;
        }

        try
        {
            var token = await GetBotTokenAsync(cfg, c.TimeoutSeconds);

            var payload = new TeamsActivity("message", text,
                string.IsNullOrWhiteSpace(replyToId) ? null : replyToId);
            var payloadJson = JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default);

            var url =
                $"{serviceUrl!.TrimEnd('/')}/v3/conversations/{Uri.EscapeDataString(conversationId!)}/activities";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            var client = httpClientFactory.CreateClient("TeamsBot");
            client.Timeout = TimeSpan.FromSeconds(c.TimeoutSeconds);

            using var response = await client.SendAsync(request);
            var body = await response.Content.ReadAsStringAsync();
            if (!response.IsSuccessStatusCode)
            {
                HandleFailure(c, nodeContext,
                    $"TeamsBotReply: Bot Framework returned {(int)response.StatusCode} — {body}");
            }
        }
        catch (Exception ex) when (ex is not MeshAdapterPipelineExecutionException)
        {
            HandleFailure(c, nodeContext, $"TeamsBotReply: {ex.Message}");
        }

        await next(dataContext, nodeContext);
    }

    private async Task<string> GetBotTokenAsync(BotConfiguration cfg, int timeoutSeconds)
    {
        var client = httpClientFactory.CreateClient("TeamsBot");
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        // Single-tenant bot → tenant authority; multi-tenant bot → botframework.com.
        var tokenUrl = string.IsNullOrWhiteSpace(cfg.AzureTenantId)
            ? MultiTenantTokenUrl
            : $"https://login.microsoftonline.com/{cfg.AzureTenantId}/oauth2/v2.0/token";

        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = cfg.ClientId,
            ["client_secret"] = cfg.ClientSecret,
            ["scope"] = BotFrameworkScope
        });

        using var response = await client.PostAsync(tokenUrl, content);
        var json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    private static void HandleFailure(TeamsBotReplyNodeConfiguration c, INodeContext nodeContext,
        string message)
    {
        if (c.ContinueOnError)
        {
            nodeContext.Error(message);
        }
        else
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext, message);
        }
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

    /// <summary>
    /// Minimal Bot Framework outbound activity (text message). Optional keys omitted when null.
    /// </summary>
    internal sealed record TeamsActivity(
        [property: JsonPropertyName("type")] string Type,
        [property: JsonPropertyName("text")] string Text,
        [property: JsonPropertyName("replyToId")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        string? ReplyToId);
}
