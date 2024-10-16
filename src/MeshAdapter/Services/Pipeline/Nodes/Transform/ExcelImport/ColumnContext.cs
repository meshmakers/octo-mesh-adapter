using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform.ExcelImport;

internal class ColumnContext
{
    private readonly List<Tuple<string, int>> _columnIndexes = [];

    public ColumnContext(JArray columns)
    {
        foreach (var column in columns)
        {
            var name = column.Value<string>("attributeName");
            if (name == null)
                continue;
            var index = column.Value<int>("columnIndex");
            _columnIndexes.Add(new(name, index));
        }
    }

    public T? GetValue<T>(JArray row, string name)
    {
        var index = _columnIndexes.FirstOrDefault(x => x.Item1 == name)?.Item2;
        return index == null ? default : row[index.Value].Value<T>();
    }

    public IEnumerable<string> GetColumnNames()
    {
        return _columnIndexes.Select(x => x.Item1);
    }
}