using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Renders an array of records into one delimited-text document: one row per array element, one
/// column per configured entry. A column is a constant, a value read from the record, or empty.
/// </summary>
[NodeConfiguration(typeof(RenderDelimitedTextNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class RenderDelimitedTextNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<RenderDelimitedTextNodeConfiguration>();

        // Configuration mistakes fail whatever else is configured: each of them would otherwise
        // let a run report success while producing a document nobody can parse.
        ValidateConfiguration(c, nodeContext);

        var columns = c.Columns!.ToList();

        // One detached read context per array item, in document order.
        var rows = new List<string>();
        var recordIndex = 0;
        foreach (var record in dataContext.SelectMatches($"{c.Path}[*]"))
        {
            rows.Add(RenderRow(record, columns, c, nodeContext, recordIndex));
            recordIndex++;
        }

        // The separator is chosen here, never taken from the operating system: the same definition
        // has to produce the same bytes on every host.
        var separator = c.LineEnding == DelimitedLineEnding.CrLf ? "\r\n" : "\n";
        var text = string.Join(separator, rows);
        if (rows.Count > 0 && c.TrailingNewLine)
        {
            text += separator;
        }

        dataContext.Set(c.TargetPath, text, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        nodeContext.Info("RenderDelimitedText: rendered {0} record(s), {1} character(s)",
            recordIndex, text.Length);

        await next(dataContext, nodeContext);
    }

    private static string RenderRow(IDataContext record, List<DelimitedColumn> columns,
        RenderDelimitedTextNodeConfiguration c, INodeContext nodeContext, int recordIndex)
    {
        var values = new string[columns.Count];
        for (var i = 0; i < columns.Count; i++)
        {
            var column = columns[i];
            values[i] = column.Value ?? ReadValue(record, column, nodeContext, recordIndex, i);
        }

        return string.Join(c.Delimiter, values);
    }

    private static string ReadValue(IDataContext record, DelimitedColumn column,
        INodeContext nodeContext, int recordIndex, int columnIndex)
    {
        if (string.IsNullOrEmpty(column.ValuePath))
        {
            return string.Empty;
        }

        var path = JsonNodePath.NormalizePathOrRelative(column.ValuePath);

        // Text conversion follows the house rule the other text-producing nodes use: a number keeps
        // its raw JSON token so no culture can reshape it, a boolean is capitalised, and an object
        // or array is refused - it would arrive as indented multi-line JSON and break the record.
        return record.GetKind(path) switch
        {
            DataKind.Undefined or DataKind.Null => string.Empty,
            DataKind.String => record.Get<string>(path) ?? string.Empty,
            DataKind.Number => record.Get<JsonNode>(path)?.ToJsonString() ?? string.Empty,
            DataKind.Boolean => record.Get<bool>(path) ? "True" : "False",
            _ => throw MeshAdapterPipelineExecutionException.DelimitedValueNotScalar(
                nodeContext, recordIndex, columnIndex, column.ValuePath)
        };
    }

    private static void ValidateConfiguration(RenderDelimitedTextNodeConfiguration c,
        INodeContext nodeContext)
    {
        if (c.Columns is null || c.Columns.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.DelimitedColumnsNotSet(nodeContext);
        }

        var index = 0;
        foreach (var column in c.Columns)
        {
            if (column is null)
            {
                throw MeshAdapterPipelineExecutionException.DelimitedColumnNull(nodeContext, index);
            }

            if (column.Value is not null && !string.IsNullOrEmpty(column.ValuePath))
            {
                throw MeshAdapterPipelineExecutionException.DelimitedColumnAmbiguous(nodeContext, index);
            }

            index++;
        }
    }
}
