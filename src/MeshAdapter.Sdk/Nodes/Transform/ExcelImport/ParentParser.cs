using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal static class ParentNameParser
{
    public static string? ParseSeparatorBased(string name)
    {
        var parts = name.Split(Constants.Delimiters,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 1)
        {
            return null;
        }

        var toRemove = parts.Last();
        return name.Remove(name.Length - toRemove.Length - 1).Trim();
    }

    public static string? ParseLayerBasedName(ColumnContext columnContext, JArray entities, int iLayer)
    {
        return iLayer == 1 ? null : columnContext.GetValue<string>(entities, "name", iLayer - 1);
    }
}