using System.Text.Json.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Internal;

internal static class AnomalyNodeHelpers
{
    /// <summary>
    /// Renders the node at <paramref name="path"/> on <paramref name="item"/> as a
    /// string, used as a grouping key. Returns null if the path does not resolve
    /// to a <see cref="JsonValue"/>.
    /// </summary>
    public static string? GetPropertyAsString(JsonNode? item, string path)
    {
        var node = JsonNodePath.Select(item, path);
        if (node is JsonValue v)
        {
            if (v.TryGetValue<string>(out var s)) return s;
            return v.ToJsonString();
        }
        return null;
    }
}
