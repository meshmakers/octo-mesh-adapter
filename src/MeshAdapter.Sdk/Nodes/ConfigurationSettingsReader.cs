using System.Globalization;
using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Reads scalar values out of a configuration entity that a node was given by
/// well-known name (its <c>SettingsConfiguration</c>), so runtime settings can live
/// in configuration instead of frozen into the pipeline definition — a redeploy then
/// never overwrites what an operator set and nothing tenant-specific leaks into the
/// seed. Nodes stay domain-agnostic: the caller supplies the attribute names to read.
/// <para>
/// AB#5345 made this the ONE settings resolver both mail triggers use
/// (<c>FromEmail@1</c> and <c>FromMicrosoftGraphEmail@1</c>): the per-node
/// <c>ResolveEffectiveConfiguration</c> is now nothing but an overlay built from these
/// readers, expressing the single rule that matters —
/// <b>a value found in the settings wins, the node property is the fallback</b>, which
/// is at the same time the migration path for every already deployed pipeline.
/// </para>
/// </summary>
/// <remarks>
/// The serialized configuration handed out by <see cref="IGlobalConfiguration.GetRawJson"/>
/// is the full runtime entity; its CK attributes live in a nested <c>"attributes"</c>
/// object (e.g. <c>{ "attributes": { "EmailImportMailbox": … } }</c>), so lookups descend
/// into that object first and fall back to the root for any flatter shape. All lookups are
/// case-insensitive; a malformed payload yields no attributes rather than throwing.
/// </remarks>
internal static class ConfigurationSettingsReader
{
    /// <summary>
    /// Returns the settings entity's attribute object (parsed once), or null when the
    /// configuration name is blank/undefined or the payload cannot be parsed. Callers
    /// then read individual attributes from the returned element.
    /// </summary>
    internal static JsonElement? TryGetAttributes(
        IGlobalConfiguration globalConfiguration, string? settingsConfiguration)
    {
        if (string.IsNullOrWhiteSpace(settingsConfiguration) ||
            !globalConfiguration.IsDefined(settingsConfiguration))
        {
            return null;
        }

        try
        {
            using var doc = JsonDocument.Parse(globalConfiguration.GetRawJson(settingsConfiguration));
            var root = doc.RootElement;
            if (root.ValueKind == JsonValueKind.Object)
            {
                foreach (var property in root.EnumerateObject())
                {
                    if (string.Equals(property.Name, "attributes", StringComparison.OrdinalIgnoreCase) &&
                        property.Value.ValueKind == JsonValueKind.Object)
                    {
                        return property.Value.Clone();
                    }
                }
            }

            return root.Clone();
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>Case-insensitive read of a non-empty string attribute; null when absent/blank/non-string.</summary>
    internal static string? ReadString(JsonElement attributes, string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            var value = property.Value.GetString();
            return string.IsNullOrWhiteSpace(value) ? null : value;
        }

        return null;
    }

    /// <summary>Case-insensitive read of a positive integer attribute (numeric or numeric string); null otherwise.</summary>
    internal static int? ReadPositiveInt(JsonElement attributes, string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(property.Value.GetString(), out var n) => n,
                _ => 0,
            };
            return value > 0 ? value : null;
        }

        return null;
    }

    /// <summary>
    /// Nullable-tolerant overload of <see cref="ReadString(JsonElement,string?)" /> so an overlay
    /// can be written as one expression per property even when the settings entity is absent
    /// (<c>TryGetAttributes</c> returned <c>null</c>).
    /// </summary>
    internal static string? ReadString(JsonElement? attributes, string? attributeName)
    {
        return attributes.HasValue ? ReadString(attributes.Value, attributeName) : null;
    }

    /// <summary>Nullable-tolerant overload of <see cref="ReadPositiveInt(JsonElement,string?)" />.</summary>
    internal static int? ReadPositiveInt(JsonElement? attributes, string? attributeName)
    {
        return attributes.HasValue ? ReadPositiveInt(attributes.Value, attributeName) : null;
    }

    /// <summary>
    /// Case-insensitive read of an integer attribute (numeric or numeric string), INCLUDING zero
    /// and negative values; null when absent, null or not a number.
    /// </summary>
    /// <remarks>
    /// Separate from <see cref="ReadPositiveInt(JsonElement,string?)" /> on purpose: for
    /// <c>maxMessagesPerPoll</c> a configured <c>0</c> is a deliberate "no limit" opt-out and must
    /// reach the node, while for a poll interval a <c>0</c> is nonsense and has to read as
    /// "not configured".
    /// </remarks>
    internal static int? ReadInt(JsonElement attributes, string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.Number when property.Value.TryGetInt32(out var n) => n,
                JsonValueKind.String when int.TryParse(property.Value.GetString(),
                    NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) => n,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>Nullable-tolerant overload of <see cref="ReadInt(JsonElement,string?)" />.</summary>
    internal static int? ReadInt(JsonElement? attributes, string? attributeName)
    {
        return attributes.HasValue ? ReadInt(attributes.Value, attributeName) : null;
    }

    /// <summary>
    /// Case-insensitive read of a boolean attribute; null when absent, null or not a boolean.
    /// </summary>
    /// <remarks>
    /// A CK <c>Boolean</c> attribute is serialized as a real JSON boolean, but the string forms
    /// <c>"true"</c>/<c>"false"</c> are accepted as well so a serializer change on the controller
    /// side cannot silently flip a switch back to its node-property fallback. Anything else —
    /// a number, an empty string — is "not configured" rather than <c>false</c>: reading an
    /// unreadable value as <c>false</c> would turn "I could not tell" into an operator decision.
    /// </remarks>
    internal static bool? ReadBool(JsonElement attributes, string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return property.Value.ValueKind switch
            {
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.String when bool.TryParse(property.Value.GetString(), out var b) => b,
                _ => null,
            };
        }

        return null;
    }

    /// <summary>Nullable-tolerant overload of <see cref="ReadBool(JsonElement,string?)" />.</summary>
    internal static bool? ReadBool(JsonElement? attributes, string? attributeName)
    {
        return attributes.HasValue ? ReadBool(attributes.Value, attributeName) : null;
    }

    /// <summary>
    /// Case-insensitive read of a date attribute; null when absent, null or unparsable.
    /// </summary>
    /// <remarks>
    /// A CK <c>DateTime</c> attribute reaches the serialized entity as an ISO-8601 string. Parsed
    /// with <see cref="DateTimeStyles.RoundtripKind" /> and the invariant culture so the same
    /// stored value means the same instant on every agent and in every container locale.
    /// </remarks>
    internal static DateTime? ReadDateTime(JsonElement attributes, string? attributeName)
    {
        if (string.IsNullOrWhiteSpace(attributeName) || attributes.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var property in attributes.EnumerateObject())
        {
            if (!string.Equals(property.Name, attributeName, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (property.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            return DateTime.TryParse(property.Value.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var parsed)
                ? parsed
                : null;
        }

        return null;
    }

    /// <summary>Nullable-tolerant overload of <see cref="ReadDateTime(JsonElement,string?)" />.</summary>
    internal static DateTime? ReadDateTime(JsonElement? attributes, string? attributeName)
    {
        return attributes.HasValue ? ReadDateTime(attributes.Value, attributeName) : null;
    }

    /// <summary>
    /// Case-insensitive read of an enum attribute by NAME; null when absent, null, blank or not a
    /// name the enum knows.
    /// </summary>
    /// <remarks>
    /// Names only, never the underlying number. The value is entered by an operator through a
    /// settings page and stored as a CK <c>String</c>; accepting a number there would make a typo
    /// ("3") select a mode by ordinal, and enum ordinals are exactly the thing that shifts when a
    /// member is inserted. An unknown name reads as "not configured", so the node property (and
    /// with it the previously deployed behaviour) stays in force.
    /// </remarks>
    internal static TEnum? ReadEnum<TEnum>(JsonElement attributes, string? attributeName)
        where TEnum : struct, Enum
    {
        var raw = ReadString(attributes, attributeName);
        if (raw is null || raw.AsSpan().Trim().IndexOfAnyExcept(
                "0123456789+- \t".AsSpan()) < 0)
        {
            // Absent, or nothing but digits/signs: `Enum.TryParse` would happily read that as the
            // underlying ordinal. Refused here so a mistyped number cannot select a mode.
            return null;
        }

        return Enum.TryParse<TEnum>(raw, ignoreCase: true, out var parsed) &&
               Enum.IsDefined(typeof(TEnum), parsed)
            ? parsed
            : null;
    }

    /// <summary>Nullable-tolerant overload of <see cref="ReadEnum{TEnum}(JsonElement,string?)" />.</summary>
    internal static TEnum? ReadEnum<TEnum>(JsonElement? attributes, string? attributeName)
        where TEnum : struct, Enum
    {
        return attributes.HasValue ? ReadEnum<TEnum>(attributes.Value, attributeName) : null;
    }
}
