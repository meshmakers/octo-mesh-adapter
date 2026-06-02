using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that sends a message to a Microsoft Teams channel via Incoming Webhook
/// </summary>
[NodeConfiguration(typeof(ReplyToTeamsChannelNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ReplyToTeamsChannelNode(
    NodeDelegate next,
    ILogger<ReplyToTeamsChannelNode> logger,
    IHttpClientFactory httpClientFactory)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<ReplyToTeamsChannelNodeConfiguration>();

        try
        {
            // Get webhook URL
            var webhookUrl = !string.IsNullOrEmpty(config.WebhookUrlPath)
                ? dataContext.Get<string>(config.WebhookUrlPath)
                : config.WebhookUrl;

            if (string.IsNullOrEmpty(webhookUrl))
            {
                nodeContext.Warning("No webhook URL configured, skipping Teams notification");
                await next(dataContext, nodeContext);
                return;
            }

            // Get message body
            string? body;
            if (!string.IsNullOrEmpty(config.MessageBodyPath))
            {
                body = dataContext.Get<string>(config.MessageBodyPath);
            }
            else
            {
                body = config.MessageBody;

                // Replace ${jsonPath} placeholders with values from the data context
                if (!string.IsNullOrEmpty(body))
                {
                    body = ResolvePlaceholders(body, dataContext);
                }
            }

            if (string.IsNullOrEmpty(body))
            {
                nodeContext.Warning("No message body found, skipping Teams notification");
                await next(dataContext, nodeContext);
                return;
            }

            // Resolve title placeholders if present
            var title = !string.IsNullOrEmpty(config.Title)
                ? ResolvePlaceholders(config.Title, dataContext)
                : null;

            // Send via webhook
            await SendWebhookMessageAsync(webhookUrl, title, body, config.ThemeColor);

            nodeContext.Info("Sent notification to Teams channel via webhook");
        }
        catch (Exception ex)
        {
            if (config.ContinueOnError)
            {
                logger.LogWarning(ex, "Failed to send Teams webhook notification");
                nodeContext.Warning("Failed to send Teams notification: {0}", ex.Message);
            }
            else
            {
                throw;
            }
        }

        await next(dataContext, nodeContext);
    }

    private static string ResolvePlaceholders(string template, IDataContext dataContext)
    {
        return Regex.Replace(template, @"\$\{([^}]+)\}", match =>
        {
            var jsonPath = match.Groups[1].Value;
            try
            {
                var s = dataContext.Get<string>(jsonPath);
                return s ?? "";
            }
            catch
            {
                return "";
            }
        });
    }

    private async Task SendWebhookMessageAsync(string webhookUrl, string? title, string body, string themeColor)
    {
        using var client = httpClientFactory.CreateClient();

        // Build Adaptive Card payload (required by Teams Workflows webhooks)
        var cardBody = new List<object>();

        if (!string.IsNullOrEmpty(title))
        {
            cardBody.Add(new
            {
                type = "TextBlock",
                size = "Medium",
                weight = "Bolder",
                text = title,
                color = "Good"
            });
        }

        cardBody.Add(new
        {
            type = "TextBlock",
            text = body,
            wrap = true
        });

        var payload = new
        {
            type = "message",
            attachments = new[]
            {
                new
                {
                    contentType = "application/vnd.microsoft.card.adaptive",
                    contentUrl = (string?)null,
                    content = new
                    {
                        type = "AdaptiveCard",
                        body = cardBody,
                        schema = "http://adaptivecards.io/schemas/adaptive-card.json",
                        version = "1.4"
                    }
                }
            }
        };

        var jsonContent = new StringContent(
            JsonSerializer.Serialize(payload),
            Encoding.UTF8,
            "application/json");

        var response = await client.PostAsync(webhookUrl, jsonContent);
        response.EnsureSuccessStatusCode();
    }
}
