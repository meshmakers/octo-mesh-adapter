using System.Net.Http;
using System.Text.Json;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Trigger node that polls a signal-cli-rest-api bridge for inbound Signal messages and
/// fires the pipeline with a batch of normalized messages. Attachment BYTES are fetched
/// from <c>GET {ApiUrl}/v1/attachments/{id}</c> and exposed as base64 in
/// <c>$.Messages[].Attachments[].Data</c> — the same shape the E-Mail assistant produces —
/// so the invoice OCR flow works unchanged. Prototype context: AB#4406 (Epic AB#3295).
/// </summary>
[NodeConfiguration(typeof(FromSignalNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FromSignalNode(
    ILogger<FromSignalNode> logger,
    IHttpClientFactory httpClientFactory,
    IChannelCallerBinder callerBinder) : ITriggerPipelineNode
{
    private CancellationTokenSource? _cancellationTokenSource;
    private Task? _pollingTask;

    public Task StartAsync(ITriggerContext context)
    {
        var c = context.NodeContext.GetNodeConfiguration<FromSignalNodeConfiguration>();

        // Bridge number / URL may live in a configuration entity instead of the pipeline
        // definition (see SettingsConfiguration) so a redeploy never overwrites operator
        // settings and nothing tenant-specific leaks into the seed. A settings value wins.
        var attrs = ConfigurationSettingsReader.TryGetAttributes(
            context.GlobalConfiguration, c.SettingsConfiguration);
        var apiUrl = (attrs.HasValue ? ConfigurationSettingsReader.ReadString(attrs.Value, c.ApiUrlAttribute) : null)
                     ?? c.ApiUrl;
        var number = (attrs.HasValue ? ConfigurationSettingsReader.ReadString(attrs.Value, c.NumberAttribute) : null)
                     ?? c.Number;

        if (string.IsNullOrWhiteSpace(apiUrl))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext, nameof(c.ApiUrl), c.SettingsConfiguration ?? nameof(c.ApiUrl));
        }

        if (string.IsNullOrWhiteSpace(number))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                context.NodeContext, nameof(c.Number), c.SettingsConfiguration ?? nameof(c.Number));
        }

        var effectiveConfig = c with { ApiUrl = apiUrl, Number = number };

        _cancellationTokenSource = new CancellationTokenSource();
        _pollingTask = Task.Run(
            () => PollForMessagesAsync(context, effectiveConfig), _cancellationTokenSource.Token);
        return Task.CompletedTask;
    }

    private async Task PollForMessagesAsync(ITriggerContext context, FromSignalNodeConfiguration c)
    {
        var apiBase = c.ApiUrl.TrimEnd('/');
        var receiveUrl = $"{apiBase}/v1/receive/{c.Number}";
        var client = httpClientFactory.CreateClient("Signal");

        while (!_cancellationTokenSource!.Token.IsCancellationRequested)
        {
            try
            {
                // /v1/receive consumes messages on read, so no processed-id tracking needed.
                var raw = await client.GetStringAsync(receiveUrl, _cancellationTokenSource.Token);

                var messages = new List<SignalMessageData>();
                using (var doc = JsonDocument.Parse(raw))
                {
                    if (doc.RootElement.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in doc.RootElement.EnumerateArray())
                        {
                            var msg = await ParseEnvelopeAsync(client, apiBase, item, c);
                            if (msg != null)
                            {
                                messages.Add(msg);
                            }
                        }
                    }
                }

                if (messages.Count > 0)
                {
                    var batch = new SignalBatch
                    {
                        Messages = messages,
                        Count = messages.Count,
                        ProcessedAt = DateTime.UtcNow
                    };

                    // AB#5126: this trigger fires one execution for a batch of messages. A caller is
                    // only unambiguous when the whole batch shares one sender number; otherwise the
                    // execution has no single identity and is treated as unresolved. True per-message
                    // identity (per-sender execution) is the per-channel WI's job (AB#5123 — phone/OTP,
                    // which also sets the real message trust). The phone number is the identifier.
                    var distinctSources = messages
                        .Select(m => m.Source)
                        .Where(s => !string.IsNullOrWhiteSpace(s))
                        .Distinct()
                        .ToList();
                    var sender = distinctSources.Count == 1
                        ? new ChannelSender(ChannelIdentifierKind.PhoneNumber, distinctSources[0]!, CallerTrustLevel.Weak)
                        : null;
                    var binding = await callerBinder.BindAsync(context.TenantId, c.CallerBinding, sender,
                        _cancellationTokenSource!.Token);
                    if (binding.Rejected)
                    {
                        logger.LogWarning("FromSignal: {Reason} Skipping batch of {Count} message(s).",
                            binding.RejectReason, messages.Count);
                    }
                    else
                    {
                        await context.ExecuteAsync(new ExecutePipelineOptions(DateTime.UtcNow)
                        {
                            VerifiedPrincipal = binding.Principal,
                            CallerTrust = binding.Trust
                        }, batch);
                        logger.LogInformation("Processed {Count} new Signal messages", messages.Count);
                    }
                }

                await Task.Delay(TimeSpan.FromSeconds(c.PollingIntervalSeconds), _cancellationTokenSource.Token);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                logger.LogError(ex, "Error while polling the Signal bridge");
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(30), _cancellationTokenSource.Token);
                }
                catch (OperationCanceledException)
                {
                    // Stop requested during the back-off delay — exit cleanly so the task
                    // completes normally and StopAsync's WaitAsync does not observe a fault.
                    break;
                }
            }
        }
    }

    /// <summary>
    /// Maps one signal-cli receive envelope to a <see cref="SignalMessageData"/>, downloading
    /// attachment bytes. Returns null for non-message events (receipts, typing) and for
    /// senders filtered out by <see cref="FromSignalNodeConfiguration.SenderFilter"/>.
    /// </summary>
    private async Task<SignalMessageData?> ParseEnvelopeAsync(
        HttpClient client, string apiBase, JsonElement item, FromSignalNodeConfiguration c)
    {
        if (!item.TryGetProperty("envelope", out var envelope))
        {
            return null;
        }

        // Prefer the phone number (human-readable + replyable) over the ACI/PNI UUID
        // that modern Signal puts in "source"; fall back to it when no number is shared.
        var source = GetString(envelope, "sourceNumber") ?? GetString(envelope, "source");
        if (!string.IsNullOrWhiteSpace(c.SenderFilter)
            && (source == null || !source.Contains(c.SenderFilter)))
        {
            return null;
        }

        if (!envelope.TryGetProperty("dataMessage", out var dataMessage)
            || dataMessage.ValueKind != JsonValueKind.Object)
        {
            // Receipt / typing / sync event — nothing to process.
            return null;
        }

        var text = GetString(dataMessage, "message");

        var attachments = new List<AttachmentData>();
        if (dataMessage.TryGetProperty("attachments", out var atts)
            && atts.ValueKind == JsonValueKind.Array)
        {
            foreach (var att in atts.EnumerateArray())
            {
                var id = GetString(att, "id");
                if (string.IsNullOrWhiteSpace(id))
                {
                    continue;
                }

                var contentType = GetString(att, "contentType") ?? "application/octet-stream";
                var filename = GetString(att, "filename");

                try
                {
                    var bytes = await client.GetByteArrayAsync(
                        $"{apiBase}/v1/attachments/{id}", _cancellationTokenSource!.Token);
                    attachments.Add(new AttachmentData
                    {
                        FileName = string.IsNullOrWhiteSpace(filename) ? id : filename,
                        ContentType = contentType,
                        Data = Convert.ToBase64String(bytes),
                        Length = bytes.LongLength
                    });
                }
                catch (Exception ex)
                {
                    logger.LogWarning(ex, "Failed to download Signal attachment {Id}", id);
                }
            }
        }

        if (string.IsNullOrWhiteSpace(text) && attachments.Count == 0)
        {
            // Empty data message (e.g. a reaction) — skip.
            return null;
        }

        return new SignalMessageData
        {
            Source = source,
            SourceName = GetString(envelope, "sourceName"),
            Timestamp = envelope.TryGetProperty("timestamp", out var ts)
                        && ts.TryGetInt64(out var tsv)
                ? tsv
                : 0,
            Message = text,
            Attachments = attachments
        };
    }

    private static string? GetString(JsonElement element, string property) =>
        element.TryGetProperty(property, out var v) && v.ValueKind == JsonValueKind.String
            ? v.GetString()
            : null;

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
                logger.LogWarning("Signal polling task did not complete within timeout");
            }
            catch (OperationCanceledException)
            {
                // The polling task was cancelled mid-request — expected on stop; not a failure.
            }
        }

        _cancellationTokenSource?.Dispose();
    }
}

/// <summary>
/// A normalized inbound Signal message. Attachment shape mirrors <c>FromEmail@1</c>
/// (<see cref="AttachmentData"/>) so the same downstream OCR / document-import nodes apply.
/// </summary>
public class SignalMessageData
{
    /// <summary>Sender number (e.g. +4366098765432).</summary>
    public string? Source { get; set; }

    /// <summary>Sender display name, when shared.</summary>
    public string? SourceName { get; set; }

    /// <summary>Signal message timestamp (epoch milliseconds).</summary>
    public long Timestamp { get; set; }

    /// <summary>Message text, if any.</summary>
    public string? Message { get; set; }

    /// <summary>Downloaded attachments (base64 in <see cref="AttachmentData.Data"/>).</summary>
    public List<AttachmentData> Attachments { get; set; } = new();
}

/// <summary>
/// A batch of inbound Signal messages handed to the pipeline as the data context root.
/// </summary>
public class SignalBatch
{
    /// <summary>The messages in this batch.</summary>
    public List<SignalMessageData> Messages { get; set; } = new();

    /// <summary>Number of messages in the batch.</summary>
    public int Count { get; set; }

    /// <summary>Timestamp when the batch was assembled.</summary>
    public DateTime ProcessedAt { get; set; }
}
