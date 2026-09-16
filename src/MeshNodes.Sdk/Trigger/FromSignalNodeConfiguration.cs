using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Trigger;

/// <summary>
/// Configuration for the FromSignal trigger node. Polls a signal-cli-rest-api bridge
/// (<c>GET {ApiUrl}/v1/receive/{Number}</c>) for inbound Signal messages and fires the
/// pipeline with a batch of normalized messages (incl. downloaded attachment bytes).
/// Inbound counterpart of <c>SignalSender@1</c>. Prototype context: AB#4406 (Epic AB#3295).
/// Number/ApiUrl resolution (AB#5145): the tenant's registered
/// <c>System.Communication/SignalChannel</c> singleton (activated via the Studio self-service,
/// AB#5143) wins and needs NO configuration here at all; the properties below are the
/// deprecated legacy fallback. With neither, the trigger stays idle instead of failing.
/// </summary>
[NodeName("FromSignal", 1)]
[NodeRequiresRunningProcess]
public record FromSignalNodeConfiguration : TriggerNodeConfiguration
{
    /// <summary>
    /// DEPRECATED legacy fallback (AB#5145): base URL of the signal-cli-rest-api bridge,
    /// e.g. <c>http://localhost:8080</c>. Only read when the tenant has no REGISTERED
    /// <c>System.Communication/SignalChannel</c>; a <see cref="SettingsConfiguration"/> value
    /// still takes precedence over this literal. May be omitted entirely on new-style pipelines.
    /// </summary>
    [PropertyGroup("Connection", 0)]
    public string ApiUrl { get; set; } = null!;

    /// <summary>
    /// DEPRECATED legacy fallback (AB#5145): the bridge's registered account number to receive
    /// for, e.g. <c>+4366012345678</c>. Only read when the tenant has no REGISTERED
    /// <c>System.Communication/SignalChannel</c>; a <see cref="SettingsConfiguration"/> value
    /// still takes precedence over this literal. May be omitted entirely on new-style pipelines.
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
    /// DEPRECATED legacy fallback (AB#5145): optional well-known name of a configuration entity
    /// that carries the bridge number / URL (e.g. the accounting app's
    /// <c>SignalImportSettings</c>), reachable from the pipeline via a
    /// <c>System.Communication/Uses</c> association; the attribute names it reads are
    /// <see cref="NumberAttribute"/> / <see cref="ApiUrlAttribute"/>, and values found here
    /// override the node properties. Only consulted when the tenant has no REGISTERED
    /// <c>System.Communication/SignalChannel</c> — new-style pipelines omit this entirely.
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
