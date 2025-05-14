using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal static class ParentNameParser
{
    public static string? ParseSeparatorBased(string name, out string itemName)
    {
        var parts = name.Split(Constants.Delimiters,
            StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        string? parentName;
        if (parts.Length == 1)
        {
            itemName = name;
            parentName = null;
        }
        else
        {
            itemName = parts.Last();
            parentName = name.Remove(name.Length - itemName.Length - 1).Trim();
        }

        // Handle case if there is a delimiter that needs to be retained
        var s = itemName.AsSpan();
        var index = s.IndexOfAny(Constants.DelimitersRetained);
        if (index == -1)
        {
            return parentName;
        }

        itemName = itemName[index..];
        return name.Remove(name.Length - itemName.Length).Trim();
    }

    public static string? ParseLayerBasedName(ColumnContext columnContext, JArray entities, int iLayer)
    {
        return iLayer == 1 ? null : columnContext.GetValueByPath<string>(entities, "name");
    }
}