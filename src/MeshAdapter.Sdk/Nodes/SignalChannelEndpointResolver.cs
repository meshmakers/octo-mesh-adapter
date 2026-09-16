using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Where the Signal bridge endpoint (account number + api URL) came from.
/// </summary>
internal enum SignalEndpointSource
{
    /// <summary>Nothing usable was found — the node must stay idle (trigger) or report (sender).</summary>
    None,

    /// <summary>The tenant's registered <c>System.Communication/SignalChannel</c> singleton.</summary>
    SignalChannel,

    /// <summary>The deprecated settings-configuration / node-property mechanism.</summary>
    LegacySettings
}

/// <summary>
/// Result of <see cref="SignalChannelEndpointResolver.Resolve"/>. <see cref="Warnings"/> carries
/// everything worth telling the operator (not-registered channel, deprecation, half-configured
/// legacy settings) as plain strings, so the resolver stays pure and each node relays them
/// through its own logging channel (ILogger for the trigger, INodeContext for the sender).
/// </summary>
internal sealed record SignalEndpointResolution(
    SignalEndpointSource Source,
    string? Number,
    string? ApiUrl,
    IReadOnlyList<string> Warnings)
{
    /// <summary>True when <see cref="Number"/> and <see cref="ApiUrl"/> are both usable.</summary>
    public bool IsConfigured => Source != SignalEndpointSource.None;
}

/// <summary>
/// Resolves the Signal bridge endpoint for <c>FromSignal@1</c> and <c>SignalSender@1</c>
/// (AB#5145). Resolution order:
/// <list type="number">
///     <item>
///         The tenant's <c>System.Communication/SignalChannel</c> singleton (the AB#5143
///         self-service entity). The communication controller ALWAYS projects it into every
///         pipeline's configuration list — no <c>Uses</c> association required — under its
///         well-known name <c>"signal-channel"</c>, which is the <see cref="IGlobalConfiguration"/>
///         dictionary key looked up here (contract doc:
///         <c>AdapterService.AddSignalChannelConfigurationAsync</c> in
///         octo-communication-controller-services). Its <c>Number</c> / <c>ApiUrl</c> are used
///         ONLY while <c>RegistrationState == Registered (2)</c>; a channel in any other state is
///         reported and treated as unconfigured, because an unregistered number cannot send or
///         receive.
///     </item>
///     <item>
///         LEGACY fallback: the settings-configuration mechanism
///         (<c>SettingsConfiguration</c> + <c>NumberAttribute</c> / <c>ApiUrlAttribute</c>,
///         e.g. the accounting app's <c>SignalImportSettings</c>) and the literal node properties.
///         Kept so existing pipeline definitions keep working, but deprecated — a warning points
///         at the SignalChannel self-service.
///     </item>
///     <item>
///         Neither → <see cref="SignalEndpointSource.None"/>. The trigger then stays idle with one
///         clear log line (no crash, no error-state loop); the sender reports and stops.
///     </item>
/// </list>
/// The controller ships the channel in EVERY registration state on purpose: only the shipped
/// state lets this resolver distinguish "present but not registered" (warn precisely) from
/// "no channel at all" (stay quiet).
/// </summary>
internal static class SignalChannelEndpointResolver
{
    /// <summary>
    /// The well-known name the AB#5143 self-service stamps on the tenant's SignalChannel
    /// singleton — and therefore the GlobalConfiguration key the controller projection uses.
    /// </summary>
    internal const string SignalChannelWellKnownName = "signal-channel";

    /// <summary>SignalRegistrationState enum value for Registered (CK model 3.34.0).</summary>
    private const int RegisteredStateValue = 2;

    private const string RegistrationStateAttribute = "RegistrationState";
    private const string NumberAttribute = "Number";
    private const string ApiUrlAttribute = "ApiUrl";

    internal static SignalEndpointResolution Resolve(IGlobalConfiguration globalConfiguration,
        string? settingsConfiguration, string? numberAttribute, string? apiUrlAttribute,
        string? legacyNumber, string? legacyApiUrl)
    {
        var warnings = new List<string>();

        var channelResolution = TryResolveFromSignalChannel(globalConfiguration, warnings);
        if (channelResolution != null)
        {
            return channelResolution with { Warnings = warnings };
        }

        var legacyResolution = TryResolveFromLegacySettings(globalConfiguration,
            settingsConfiguration, numberAttribute, apiUrlAttribute, legacyNumber, legacyApiUrl,
            warnings);
        if (legacyResolution != null)
        {
            return legacyResolution with { Warnings = warnings };
        }

        return new SignalEndpointResolution(SignalEndpointSource.None, null, null, warnings);
    }

    private static SignalEndpointResolution? TryResolveFromSignalChannel(
        IGlobalConfiguration globalConfiguration, List<string> warnings)
    {
        if (!globalConfiguration.IsDefined(SignalChannelWellKnownName))
        {
            return null;
        }

        var attributes = ConfigurationSettingsReader.TryGetAttributes(
            globalConfiguration, SignalChannelWellKnownName);
        if (attributes == null)
        {
            warnings.Add(
                $"the tenant's SignalChannel configuration entry ('{SignalChannelWellKnownName}') could not be parsed — treated as unconfigured.");
            return null;
        }

        var (isRegistered, stateDescription) = ReadRegistrationState(attributes.Value);
        if (!isRegistered)
        {
            warnings.Add(
                $"the tenant's SignalChannel is present but not Registered (state: {stateDescription}) — treated as unconfigured until the number registration completes (Studio → Communication → Signal channel).");
            return null;
        }

        var number = ConfigurationSettingsReader.ReadString(attributes.Value, NumberAttribute);
        var apiUrl = ConfigurationSettingsReader.ReadString(attributes.Value, ApiUrlAttribute);
        if (string.IsNullOrWhiteSpace(number) || string.IsNullOrWhiteSpace(apiUrl))
        {
            // Never produced by the self-service (both are mandatory on the CK type) — but a
            // half-written entity must degrade loudly, not crash the node.
            warnings.Add(
                "the tenant's SignalChannel is Registered but carries no Number/ApiUrl — treated as unconfigured.");
            return null;
        }

        return new SignalEndpointResolution(SignalEndpointSource.SignalChannel, number, apiUrl,
            warnings);
    }

    private static SignalEndpointResolution? TryResolveFromLegacySettings(
        IGlobalConfiguration globalConfiguration, string? settingsConfiguration,
        string? numberAttribute, string? apiUrlAttribute, string? legacyNumber,
        string? legacyApiUrl, List<string> warnings)
    {
        var attributes = ConfigurationSettingsReader.TryGetAttributes(
            globalConfiguration, settingsConfiguration);
        var number = (attributes.HasValue
                         ? ConfigurationSettingsReader.ReadString(attributes.Value, numberAttribute)
                         : null)
                     ?? NullIfBlank(legacyNumber);
        var apiUrl = (attributes.HasValue
                         ? ConfigurationSettingsReader.ReadString(attributes.Value, apiUrlAttribute)
                         : null)
                     ?? NullIfBlank(legacyApiUrl);

        if (number == null && apiUrl == null)
        {
            return null;
        }

        if (number == null || apiUrl == null)
        {
            var missing = number == null ? "the account number" : "the bridge api URL";
            warnings.Add(
                $"the legacy Signal settings supply only part of the endpoint ({missing} is missing) — treated as unconfigured. Register the number via the tenant's SignalChannel instead.");
            return null;
        }

        warnings.Add(
            "resolving the Signal bridge from legacy settings (settingsConfiguration/number/apiUrl, e.g. SignalImportSettings) is deprecated — register the number as the tenant's System.Communication/SignalChannel; the nodes then resolve it automatically (AB#5145).");
        return new SignalEndpointResolution(SignalEndpointSource.LegacySettings, number, apiUrl,
            warnings);
    }

    /// <summary>
    /// Reads <c>RegistrationState</c> liberally: the serialized entity carries the enum as a JSON
    /// number (STJ default for a boxed enum), but a numeric string or the enum NAME are accepted
    /// too so a serializer change on the controller side cannot silently un-register every
    /// channel. Absent/unreadable counts as NOT registered (fail closed — an unregistered number
    /// cannot send).
    /// </summary>
    private static (bool IsRegistered, string StateDescription) ReadRegistrationState(
        JsonElement attributes)
    {
        if (attributes.ValueKind != JsonValueKind.Object)
        {
            return (false, "unknown");
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, RegistrationStateAttribute,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            switch (property.Value.ValueKind)
            {
                case JsonValueKind.Number when property.Value.TryGetInt32(out var numeric):
                    return (numeric == RegisteredStateValue,
                        numeric.ToString(CultureInfo.InvariantCulture));
                case JsonValueKind.String:
                    var s = property.Value.GetString();
                    if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture,
                            out var parsed))
                    {
                        return (parsed == RegisteredStateValue,
                            parsed.ToString(CultureInfo.InvariantCulture));
                    }

                    return (string.Equals(s, "Registered", StringComparison.OrdinalIgnoreCase),
                        s ?? "unknown");
                default:
                    return (false, property.Value.ValueKind.ToString());
            }
        }

        return (false, "absent");
    }

    private static string? NullIfBlank(string? value)
    {
        return string.IsNullOrWhiteSpace(value) ? null : value;
    }
}
