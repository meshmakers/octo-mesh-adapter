using System.Text.Json;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Reads scalar values out of a configuration entity that a node was given by
/// well-known name (its <c>SettingsConfiguration</c>), so runtime settings can live
/// in configuration instead of frozen into the pipeline definition — a redeploy then
/// never overwrites what an operator set and nothing tenant-specific leaks into the
/// seed. Nodes stay domain-agnostic: the caller supplies the attribute names to read.
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
}
