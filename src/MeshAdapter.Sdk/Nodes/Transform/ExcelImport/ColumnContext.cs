using Meshmakers.Octo.ConstructionKit.Contracts;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;


internal class ColumnContext
{
    private record ColumnIndex(CkId<CkTypeId> CkTypeId, string AttributePath,  int Index, int Layer);

    private readonly List<ColumnIndex> _columnIndexes = [];

    public ColumnContext(JArray columns)
    {
        foreach (var c in columns)
        {
            var column = (JObject)c;
            var attributePath = column.Value<string>("attributePath");
            if (attributePath == null)
                continue;
            var ckTypeId = column.Value<string>("ckTypeId") ?? "Basic/TreeNode";
            var index = column.Value<int>("columnIndex");
            var layer = 1;
            if (column.TryGetValue("layer", StringComparison.InvariantCultureIgnoreCase, out var layerValue))
            {
                layer = layerValue.Value<int>();
            }
            _columnIndexes.Add(new(ckTypeId, attributePath, index, layer));
        }
    }

    public T? GetValue<T>(JArray row, string attributePath, int layer = 1)
    {
        var index = _columnIndexes.FirstOrDefault(x => x.AttributePath == attributePath && x.Layer == layer)?.Index;
        return index == null ? default : row[index.Value].Value<T>();
    }

    public CkId<CkTypeId> GetCkTypeId(JArray row, int layer = 1)
    {
        return _columnIndexes.FirstOrDefault(x => x.Layer == layer)?.CkTypeId ?? new CkId<CkTypeId>("Basic/TreeNode");
    }

    public IEnumerable<string> GetAttributePaths(int layer = 1)
    {
        return _columnIndexes.Where(x=>x.Layer == layer).Select(x => x.AttributePath);
    }
    
    public int GetMaxLayer()
    {
        return _columnIndexes.Max(x => x.Layer);
    }
}