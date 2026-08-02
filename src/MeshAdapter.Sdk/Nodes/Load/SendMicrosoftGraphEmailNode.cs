using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Markdig;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline node that sends an email via Microsoft Graph
/// (<c>POST https://graph.microsoft.com/v1.0/users/{mailbox}/sendMail</c>),
/// reusing the app-only credentials of a <c>MicrosoftGraphConfiguration</c> resolved
/// by name (the same config <c>FromMicrosoftGraphEmail@1</c> uses). Requires Graph
/// application permission <c>Mail.Send</c>. The body is read from <c>Path</c> as
/// Markdown and rendered to HTML.
/// </summary>
/// <param name="next">Next node in the pipeline.</param>
/// <param name="etlContext">The ETL context providing global configuration.</param>
/// <param name="httpClientFactory">HttpClient factory.</param>
[NodeConfiguration(typeof(SendMicrosoftGraphEmailNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SendMicrosoftGraphEmailNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IHttpClientFactory httpClientFactory) : IPipelineNode
{
    private const string GraphBaseUrl = "https://graph.microsoft.com/v1.0";
    private const string GraphScope = "https://graph.microsoft.com/.default";

    /// <summary>
    /// App-only Graph credentials resolved from the MicrosoftGraphConfiguration by name.
    /// </summary>
    // ReSharper disable once ClassNeverInstantiated.Global
    // Internal (not private) so the unit test can stub GlobalConfiguration.GetValue<GraphConfiguration>.
    internal record GraphConfiguration
    {
        // ReSharper disable UnusedAutoPropertyAccessor.Global
        public required string AzureTenantId { get; init; }
        public required string ClientId { get; init; }
        public required string ClientSecret { get; init; }
        // ReSharper restore UnusedAutoPropertyAccessor.Global
    }

    private readonly MarkdownPipeline _pipeline =
        new MarkdownPipelineBuilder().UseAdvancedExtensions().Build();

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SendMicrosoftGraphEmailNodeConfiguration>();

        if (!etlContext.GlobalConfiguration.IsDefined(c.ServerConfiguration))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, nameof(c.ServerConfiguration), c.ServerConfiguration);
        }
        var cfg = etlContext.GlobalConfiguration.GetValue<GraphConfiguration>(c.ServerConfiguration);

        var mailbox = ResolveStringValue(dataContext, c.MailboxPath, c.Mailbox);
        if (string.IsNullOrWhiteSpace(mailbox))
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                "SendMicrosoftGraphEmail: sender mailbox is not set (Mailbox/MailboxPath).");
        }

        var subject = dataContext.Get<string>(c.SubjectPath);
        if (subject == null)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                $"SendMicrosoftGraphEmail: subject is not set (SubjectPath={c.SubjectPath}).");
        }

        var body = dataContext.Get<string>(c.Path);
        if (body == null)
        {
            throw MeshAdapterPipelineExecutionException.InvalidValue(nodeContext,
                $"SendMicrosoftGraphEmail: body is not set (Path={c.Path}).");
        }

        var recipients = ResolveRecipients(dataContext, c.ToPath);
        if (recipients.Count == 0)
        {
            HandleFailure(c, nodeContext,
                $"SendMicrosoftGraphEmail: no recipients found (ToPath={c.ToPath}).");
            await next(dataContext, nodeContext);
            return;
        }

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.Info(
                "SendMicrosoftGraphEmail (dry-run): would send '{0}' from {1} to {2} recipient(s)",
                subject, mailbox, recipients.Count);
            await next(dataContext, nodeContext);
            return;
        }

        try
        {
            var token = await GetAccessTokenAsync(cfg, c.TimeoutSeconds);

            var bodyInHtml = Markdown.ToHtml(body, _pipeline);
            var payload = new GraphSendMail(
                new GraphMessage(
                    subject,
                    new GraphBody("HTML", bodyInHtml),
                    recipients.Select(r => new GraphRecipient(new GraphEmailAddress(r))).ToList()),
                SaveToSentItems: true);
            var payloadJson = JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default);

            var url = $"{GraphBaseUrl}/users/{Uri.EscapeDataString(mailbox)}/sendMail";

            using var request = new HttpRequestMessage(HttpMethod.Post, url);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Content = new StringContent(payloadJson, Encoding.UTF8, "application/json");

            var client = httpClientFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(c.TimeoutSeconds);

            using var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                var responseBody = await response.Content.ReadAsStringAsync();
                HandleFailure(c, nodeContext,
                    $"SendMicrosoftGraphEmail: Graph returned {(int)response.StatusCode} — {responseBody}");
            }
        }
        catch (Exception ex) when (ex is not MeshAdapterPipelineExecutionException)
        {
            HandleFailure(c, nodeContext, $"SendMicrosoftGraphEmail: {ex.Message}");
        }

        await next(dataContext, nodeContext);
    }

    private async Task<string> GetAccessTokenAsync(GraphConfiguration cfg, int timeoutSeconds)
    {
        var client = httpClientFactory.CreateClient();
        client.Timeout = TimeSpan.FromSeconds(timeoutSeconds);

        var tokenUrl = $"https://login.microsoftonline.com/{cfg.AzureTenantId}/oauth2/v2.0/token";
        using var content = new FormUrlEncodedContent(new Dictionary<string, string>
        {
            ["grant_type"] = "client_credentials",
            ["client_id"] = cfg.ClientId,
            ["client_secret"] = cfg.ClientSecret,
            ["scope"] = GraphScope
        });

        using var response = await client.PostAsync(tokenUrl, content);
        var json = await response.Content.ReadAsStringAsync();
        response.EnsureSuccessStatusCode();

        using var doc = JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("access_token").GetString()!;
    }

    /// <summary>
    /// Reads the recipient(s) from the data context — accepts either an array of
    /// addresses or a single string.
    /// </summary>
    private static List<string> ResolveRecipients(IDataContext dataContext, string toPath)
    {
        var array = dataContext.GetArray<string>(toPath);
        if (array != null)
        {
            return array.Where(a => !string.IsNullOrWhiteSpace(a)).Select(a => a!).ToList();
        }

        var single = dataContext.Get<string>(toPath);
        return string.IsNullOrWhiteSpace(single) ? [] : [single!];
    }

    private static void HandleFailure(SendMicrosoftGraphEmailNodeConfiguration c,
        INodeContext nodeContext, string message)
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

    // Microsoft Graph sendMail payload shape.
    internal sealed record GraphSendMail(
        [property: JsonPropertyName("message")] GraphMessage Message,
        [property: JsonPropertyName("saveToSentItems")] bool SaveToSentItems);

    internal sealed record GraphMessage(
        [property: JsonPropertyName("subject")] string Subject,
        [property: JsonPropertyName("body")] GraphBody Body,
        [property: JsonPropertyName("toRecipients")] IReadOnlyList<GraphRecipient> ToRecipients);

    internal sealed record GraphBody(
        [property: JsonPropertyName("contentType")] string ContentType,
        [property: JsonPropertyName("content")] string Content);

    internal sealed record GraphRecipient(
        [property: JsonPropertyName("emailAddress")] GraphEmailAddress EmailAddress);

    internal sealed record GraphEmailAddress(
        [property: JsonPropertyName("address")] string Address);
}
