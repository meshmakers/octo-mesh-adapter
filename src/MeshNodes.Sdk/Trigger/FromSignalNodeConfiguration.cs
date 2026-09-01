using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for the FromSignal trigger node. Polls a signal-cli-rest-api bridge
/// (<c>GET {ApiUrl}/v1/receive/{Number}</c>) for inbound Signal messages and fires the
/// pipeline with a batch of normalized messages (incl. downloaded attachment bytes).
/// Inbound counterpart of <c>SignalSender@1</c>. Prototype context: AB#4406 (Epic AB#3295).
/// </summary>
[NodeName("FromSignal", 1)]
[NodeRequiresRunningProcess]
public record FromSignalNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// Base URL of the signal-cli-rest-api bridge, e.g. <c>http://localhost:8080</c>.
    /// Optional when <see cref="SettingsConfiguration"/> supplies it (the settings value
    /// takes precedence), so it need not be hard-coded in the pipeline definition.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// The bridge's registered account number to receive for, e.g. <c>+4366012345678</c>.
    /// Optional when <see cref="SettingsConfiguration"/> supplies it (the settings value
    /// takes precedence).
    /// </summary>
    [PropertyGroup("Connection", 1)]
    public string Number { get; set; } = null!;

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

    /// <summary>
    /// Optional well-known name of a configuration entity that carries the bridge
    /// number / URL, so they live in configuration instead of the pipeline definition
    /// (a redeploy never overwrites what an operator set, and nothing tenant-specific
    /// leaks into the seed). Reachable from the pipeline via a
    /// <c>System.Communication/Uses</c> association. The node stays domain-agnostic —
    /// the attribute names it reads are <see cref="NumberAttribute"/> /
    /// <see cref="ApiUrlAttribute"/>. Values found here override the node properties.
    /// </summary>
    [PropertyGroup("Settings", 0)]
    public string? SettingsConfiguration { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the bridge number.</summary>
    [PropertyGroup("Settings", 1)]
    public string? NumberAttribute { get; set; }

    /// <summary>Attribute name on <see cref="SettingsConfiguration"/> holding the bridge base URL.</summary>
    [PropertyGroup("Settings", 2)]
    public string? ApiUrlAttribute { get; set; }
}
