namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform.ExcelImport;

internal static class ParentNameParser
{
    public static string? Parse(string name)
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
}