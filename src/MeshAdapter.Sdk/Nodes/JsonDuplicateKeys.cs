using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Collapses repeated object keys in foreign JSON (AB#5348).
/// <para>
/// A JSON object is <em>allowed</em> to repeat a key — RFC 8259 leaves the outcome to the parser —
/// and the two System.Text.Json representations disagree about it. <see cref="JsonDocument" /> /
/// <see cref="JsonElement" /> keep every occurrence and never complain; <see cref="JsonObject" />
/// is backed by a dictionary and throws
/// <c>ArgumentException: An item with the same key has already been added</c> the moment that
/// dictionary is materialised. Crucially that happens <em>lazily</em>: parsing succeeds, and the
/// throw lands on whoever first navigates into the offending object — in a pipeline that is a node
/// several steps downstream of the one that produced the value, far outside any
/// <c>continueOnError</c> the producing node has. A duplicate key therefore cannot be caught, only
/// prevented: it has to be removed before the value enters the data context.
/// </para>
/// <para>
/// The resolution is <b>last occurrence wins</b> — the same rule <c>JSON.parse</c>, the JSON
/// merge-patch semantics and <see cref="JsonObject" />'s own indexer already use, so the value a
/// downstream node reads is the one it would have read from any other JSON stack. Objects nested
/// inside arrays are covered; key comparison is ordinal, matching <see cref="JsonObject" />'s
/// default case-sensitive lookup.
/// </para>
/// </summary>
internal static class JsonDuplicateKeys
{
    /// <summary>
    /// Finds the first repeated object key anywhere in <paramref name="element" /> (depth-first,
    /// document order) and returns <c>true</c> with its name. Pure inspection over the
    /// <see cref="JsonElement" /> reader, which tolerates duplicates — nothing here materialises a
    /// <see cref="JsonObject" />, so this never throws on the very input it looks for.
    /// </summary>
    internal static bool TryFindDuplicateKey(JsonElement element, out string? duplicateKey)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                HashSet<string>? seen = null;
                foreach (var property in element.EnumerateObject())
                {
                    seen ??= new HashSet<string>(StringComparer.Ordinal);
                    if (!seen.Add(property.Name))
                    {
                        duplicateKey = property.Name;
                        return true;
                    }

                    if (TryFindDuplicateKey(property.Value, out duplicateKey))
                    {
                        return true;
                    }
                }

                break;
            }
            case JsonValueKind.Array:
            {
                foreach (var item in element.EnumerateArray())
                {
                    if (TryFindDuplicateKey(item, out duplicateKey))
                    {
                        return true;
                    }
                }

                break;
            }
        }

        duplicateKey = null;
        return false;
    }

    /// <summary>
    /// Rebuilds <paramref name="element" /> as a detached <see cref="JsonNode" /> tree in which
    /// every object key appears once, carrying its LAST value. Scalars are cloned, so the result
    /// stays valid after the source document is disposed.
    /// </summary>
    internal static JsonNode? RewriteLastValueWins(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
            {
                var result = new JsonObject();
                foreach (var property in element.EnumerateObject())
                {
                    // The indexer replaces an existing entry instead of adding a second one —
                    // this single assignment IS the last-wins rule.
                    result[property.Name] = RewriteLastValueWins(property.Value);
                }

                return result;
            }
            case JsonValueKind.Array:
            {
                var result = new JsonArray();
                foreach (var item in element.EnumerateArray())
                {
                    result.Add(RewriteLastValueWins(item));
                }

                return result;
            }
            case JsonValueKind.Null:
            case JsonValueKind.Undefined:
                return null;
            default:
                return JsonValue.Create(element.Clone());
        }
    }
}
