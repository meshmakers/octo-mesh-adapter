namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;

internal static class Constants
{
    public const string TreePathImportType = "TreePath";
    public const string TreeColumnImportType = "TreeColumns";
    public const string TreeOrderImportType = "Order";

    /// <summary>
    /// Delimiters for parsing the name of the entity, the delimiter will be ignored to the name
    /// </summary>
    public static readonly char[] Delimiters = [' ', '.'];

    /// <summary>
    /// Delimiters for parsing the name of the entity, the delimiter will be retained to the name
    /// </summary>
    public static readonly char[] DelimitersRetained = ['=', '.',  '-'];
}