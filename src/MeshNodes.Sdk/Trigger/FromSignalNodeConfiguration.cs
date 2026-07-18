using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for the FromSignal trigger node. Polls a signal-cli-rest-api bridge
/// (<c>GET {ApiUrl}/v1/receive/{Number}</c>) for inbound Signal messages and fires the
/// pipeline with a batch of normalized messages (incl. downloaded attachment bytes).
/// Inbound counterpart of <c>SignalSender@1</c>. Prototype context: AB#4406 (Epic AB#3295).
/// </summary>
[NodeName("FromSignal", 1)]
public record FromSignalNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// Base URL of the signal-cli-rest-api bridge, e.g. <c>http://localhost:8080</c>.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public required string ApiUrl { get; set; }

    /// <summary>
    /// The bridge's registered account number to receive for, e.g. <c>+4366012345678</c>.
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public required string Number { get; set; }

    /// <summary>
    /// Polling interval in seconds. The bridge's /v1/receive endpoint consumes messages
    /// on read, so each poll returns only new ones. Default 5.
    /// </summary>
    [PropertyGroup("Timing", 0)]
    public int PollingIntervalSeconds { get; set; } = 5;

    /// <summary>
    /// Optional filter for the sender number (contains match). When set, only messages
    /// from matching senders fire the pipeline — a lightweight allow-list.
    /// </summary>
    [PropertyGroup("Query", 0)]
    public string? SenderFilter { get; set; }
}
