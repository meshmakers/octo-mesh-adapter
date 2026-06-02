using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal class ColumnContext
{
    public enum ColumnType
    {
        Ignore = 0,
        Scalar = 1,
        Tree = 2
    }

    public enum ActionType
    {
        Create = 0,
        AssignByWellKnownName = 1,
    }

    internal record ColumnIndex(
        RtCkId<CkTypeId>? CkTypeId,
        string AttributePath,
        int Index,
        int Layer,
        ColumnType? ColumnType2, ActionType? Action);

    private readonly List<ColumnIndex> _columnIndexes = [];

    public ColumnContext(JsonArray columns)
    {
        foreach (var c in columns)
        {
            if (c is not JsonObject column) continue;

            var attributePath = column["attributePath"]?.GetValue<string>();
            if (attributePath == null)
            {
                continue;
            }

            string ckTypeId = "Basic/TreeNode";
            if (column["ckTypeId"] is JsonNode ckTypeIdValue)
            {
                ckTypeId = ckTypeIdValue.GetValue<string>();
            }

            var index = CoerceValue<int>(column["columnIndex"]);
            var layer = 1;
            if (TryGetCaseInsensitive(column, "layer") is JsonNode layerValue)
            {
                layer = CoerceValue<int>(layerValue);
            }

            ColumnType? columnType = null;
            if (TryGetCaseInsensitive(column, "columnType") is JsonNode columnTypeValue)
            {
                columnType = (ColumnType)CoerceValue<int>(columnTypeValue);
            }

            ActionType? actionType = null;
            if (TryGetCaseInsensitive(column, "actionType") is JsonNode actionTypeValue)
            {
                actionType = (ActionType)CoerceValue<int>(actionTypeValue);
            }

            _columnIndexes.Add(new(new RtCkId<CkTypeId>(ckTypeId), attributePath, index, layer, columnType, actionType));
        }
    }

    private static JsonNode? TryGetCaseInsensitive(JsonObject obj, string name)
    {
        foreach (var kvp in obj)
        {
            if (string.Equals(kvp.Key, name, StringComparison.InvariantCultureIgnoreCase))
            {
                return kvp.Value;
            }
        }
        return null;
    }

    public T? GetValueByPath<T>(JsonArray row, string attributePath, int layer = 1)
    {
        var index = _columnIndexes.FirstOrDefault(x => x.AttributePath == attributePath && x.Layer == layer)?.Index;
        if (index == null) return default;
        return CoerceValue<T>(row[index.Value]);
    }

    public T? GetValueByIndex<T>(JsonArray row, int index)
    {
        return CoerceValue<T>(row[index]);
    }

    /// <summary>
    /// Reads a scalar cell as <typeparamref name="T"/>, coercing across JSON scalar kinds the
    /// way Newtonsoft's <c>JToken.Value&lt;T&gt;()</c> did before the STJ migration: a JSON number
    /// or boolean read as a string yields its invariant text (<c>42</c> → <c>"42"</c>,
    /// <c>true</c> → <c>"True"</c>), and a numeric <typeparamref name="T"/> read from a JSON string
    /// is parsed. Excel cells routinely arrive as JSON numbers/booleans, and STJ's strict
    /// <c>JsonValue.GetValue&lt;T&gt;()</c> throws on any type mismatch. Objects/arrays are left to
    /// the strict reader (unchanged).
    /// </summary>
    private static T? CoerceValue<T>(JsonNode? node)
    {
        if (node is null) return default;
        if (node is not JsonValue value) return node.GetValue<T>();

        // Fast path: the node already holds the requested type (or AllowReadingFromString applies).
        try
        {
            return value.GetValue<T>();
        }
        catch (InvalidOperationException)
        {
            // Scalar kind mismatch (e.g. number read as string) — coerce below.
        }
        catch (FormatException)
        {
            // e.g. GetValue<int>() on a non-numeric string — coerce below.
        }

        // Coerce by JSON kind, not by guessing the backing CLR numeric type: TryGetValue<long>
        // does not cross-convert for in-memory JsonValues, so reading the number's raw token
        // ("42") works for both parsed and in-memory nodes. Mirrors Newtonsoft's Value<T>().
        object? raw = value.GetValueKind() switch
        {
            JsonValueKind.String => value.GetValue<string>(),
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.Number => value.ToJsonString(),
            _ => null
        };

        if (raw is null) return default;

        var targetType = Nullable.GetUnderlyingType(typeof(T)) ?? typeof(T);
        return (T?)Convert.ChangeType(raw, targetType, CultureInfo.InvariantCulture);
    }

    public RtCkId<CkTypeId> GetCkTypeId(int layer = 1, string ckTypeId = "Basic/TreeNode")
    {
        return _columnIndexes.FirstOrDefault(x => x.Layer == layer)?.CkTypeId ?? new RtCkId<CkTypeId>(ckTypeId);
    }

    public ActionType GetActionType(int layer = 1)
    {
        return _columnIndexes.FirstOrDefault(x => x.Layer == layer)?.Action ?? ActionType.Create;
    }

    public IEnumerable<ColumnIndex> GetColumns(int layer = 1)
    {
        return _columnIndexes.Where(x => x.Layer == layer);
    }

    public int GetMaxLayer()
    {
        return _columnIndexes.Max(x => x.Layer);
    }
}