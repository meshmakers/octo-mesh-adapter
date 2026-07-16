using System.Net.Http;
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
/// Pipeline node that sends a Signal message (with an optional attachment) through a
/// signal-cli-rest-api bridge (<c>POST {ApiUrl}/v2/send</c>). Outbound counterpart of
/// <c>FromSignal@1</c>. Prototype context: AB#4406 (Epic AB#3295).
/// </summary>
/// <param name="next">Next node in the pipeline.</param>
/// <param name="httpClientFactory">HttpClient factory. Uses the named client "Signal".</param>
[NodeConfiguration(typeof(SignalSenderNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class SignalSenderNode(
    NodeDelegate next,
    IHttpClientFactory httpClientFactory) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SignalSenderNodeConfiguration>();

        var recipient = ResolveStringValue(dataContext, c.RecipientPath, c.Recipient);
        if (string.IsNullOrWhiteSpace(recipient))
        {
            nodeContext.Error("SignalSender: recipient is not set (Recipient/RecipientPath)");
            return;
        }

        var message = ResolveStringValue(dataContext, c.MessagePath, c.Message) ?? string.Empty;

        // Optional attachment: build a signal-cli-rest-api data URI so the filename and
        // content type survive (data:<contentType>;filename=<name>;base64,<data>).
        IReadOnlyList<string>? attachments = null;
        var attachmentBase64 = ResolveStringValue(dataContext, c.AttachmentBase64Path, null);
        if (!string.IsNullOrWhiteSpace(attachmentBase64))
        {
            var contentType = ResolveStringValue(dataContext, c.AttachmentContentTypePath, c.AttachmentContentType)
                              ?? "application/octet-stream";
            var filename = ResolveStringValue(dataContext, c.AttachmentFilenamePath, c.AttachmentFilename);
            var prefix = string.IsNullOrWhiteSpace(filename)
                ? $"data:{contentType};base64,"
                : $"data:{contentType};filename={filename};base64,";
            attachments = new[] { prefix + attachmentBase64 };
        }

        var payload = new SignalSendPayload(c.Number, new[] { recipient }, message, attachments);
        var payloadJson = JsonSerializer.Serialize(payload, SystemTextJsonOptions.Default);

        var url = $"{c.ApiUrl.TrimEnd('/')}/v2/send";

        var client = httpClientFactory.CreateClient("Signal");
        client.Timeout = TimeSpan.FromSeconds(c.TimeoutSeconds);

        using var content = new StringContent(payloadJson, Encoding.UTF8, "application/json");
        using var response = await client.PostAsync(url, content);
        var body = await response.Content.ReadAsStringAsync();
        if (!response.IsSuccessStatusCode)
        {
            nodeContext.Error("SignalSender: bridge returned {0} — {1}", (int)response.StatusCode, body);
            return;
        }

        if (!string.IsNullOrWhiteSpace(c.TargetPath))
        {
            JsonNode? responseJson = null;
            try
            {
                responseJson = JsonNode.Parse(body) as JsonObject;
            }
            catch (Exception)
            {
                // Non-JSON response (e.g. empty body) — store the raw text instead.
            }

            if (responseJson != null)
            {
                dataContext.Set(c.TargetPath, responseJson, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode);
            }
            else
            {
                dataContext.Set(c.TargetPath, body, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode);
            }
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

    /// <summary>
    /// signal-cli-rest-api <c>/v2/send</c> request body. Optional keys omitted when null.
    /// </summary>
    internal sealed record SignalSendPayload(
        [property: JsonPropertyName("number")] string Number,
        [property: JsonPropertyName("recipients")] IReadOnlyList<string> Recipients,
        [property: JsonPropertyName("message")] string Message,
        [property: JsonPropertyName("base64_attachments")]
        [property: JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
        IReadOnlyList<string>? Base64Attachments);
}
