using Meshmakers.Octo.ConstructionKit.Contracts;
using Newtonsoft.Json.Linq;

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
        CkId<CkTypeId>? CkTypeId,
        string AttributePath,
        int Index,
        int Layer,
        ColumnType? ColumnType2, ActionType? Action);

    private readonly List<ColumnIndex> _columnIndexes = [];

    public ColumnContext(JArray columns)
    {
        foreach (var c in columns)
        {
            var column = (JObject)c;
            var attributePath = column.Value<string>("attributePath");
            if (attributePath == null)
            {
                continue;
            }

            string ckTypeId = "Basic/TreeNode";
            if (column.TryGetValue("ckTypeId", out var ckTypeIdValue))
            {
                ckTypeId = ckTypeIdValue.Value<string>()!;
            }

            var index = column.Value<int>("columnIndex");
            var layer = 1;
            if (column.TryGetValue("layer", StringComparison.InvariantCultureIgnoreCase, out var layerValue))
            {
                layer = layerValue.Value<int>();
            }

            ColumnType? columnType = null;
            if (column.TryGetValue("columnType", StringComparison.InvariantCultureIgnoreCase, out var columnTypeValue))
            {
                columnType = (ColumnType)columnTypeValue.Value<int>();
            }

            ActionType? actionType = null;
            if (column.TryGetValue("actionType", StringComparison.InvariantCultureIgnoreCase, out var actionTypeValue))
            {
                actionType = (ActionType)actionTypeValue.Value<int>();
            }

            _columnIndexes.Add(new(new CkId<CkTypeId>(ckTypeId), attributePath, index, layer, columnType, actionType));
        }
    }

    public T? GetValueByPath<T>(JArray row, string attributePath, int layer = 1)
    {
        var index = _columnIndexes.FirstOrDefault(x => x.AttributePath == attributePath && x.Layer == layer)?.Index;
        return index == null ? default : row[index.Value].Value<T>();
    }

    public T? GetValueByIndex<T>(JArray row, int index)
    {
        return row[index].Value<T>();
    }

    public CkId<CkTypeId> GetCkTypeId(int layer = 1, string ckTypeId = "Basic/TreeNode")
    {
        return _columnIndexes.FirstOrDefault(x => x.Layer == layer)?.CkTypeId ?? new CkId<CkTypeId>(ckTypeId);
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