using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform.ExcelImport;


internal class ColumnContext
{
    private record ColumnIndex(string Name, int Index, int Layer);

    private readonly List<ColumnIndex> _columnIndexes = [];

    public ColumnContext(JArray columns)
    {
        foreach (var c in columns)
        {
            var column = (JObject)c;
            var name = column.Value<string>("attributeName");
            if (name == null)
                continue;
            var index = column.Value<int>("columnIndex");
            var layer = 1;
            if (column.TryGetValue("layer", StringComparison.InvariantCultureIgnoreCase, out var layerValue))
            {
                layer = layerValue.Value<int>();
            }
            _columnIndexes.Add(new(name, index, layer));
        }
    }

    public T? GetValue<T>(JArray row, string name, int layer = 1)
    {
        var index = _columnIndexes.FirstOrDefault(x => x.Name == name && x.Layer == layer)?.Index;
        return index == null ? default : row[index.Value].Value<T>();
    }

    public IEnumerable<string> GetColumnNames(int layer = 1)
    {
        return _columnIndexes.Where(x=>x.Layer == layer).Select(x => x.Name);
    }
    
    public int GetMaxLayer()
    {
        return _columnIndexes.Max(x => x.Layer);
    }
}