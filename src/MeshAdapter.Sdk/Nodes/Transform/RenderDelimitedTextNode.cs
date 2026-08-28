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

        // An empty batch is legitimate; a path that is not an array is a wiring mistake. Producing
        // a silently empty document from a mis-typed path is worse than failing here.
        if (dataContext.GetKind(c.Path) != DataKind.Array)
        {
            throw MeshAdapterPipelineExecutionException.DelimitedSourceNotAnArray(nodeContext, c.Path);
        }

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
        var lineEnding = c.LineEnding ?? RenderDelimitedTextNodeConfiguration.DefaultLineEnding;
        var separator = lineEnding == DelimitedLineEnding.CrLf ? "\r\n" : "\n";
        var text = string.Join(separator, rows);
        if (rows.Count > 0 &&
            (c.TrailingNewLine ?? RenderDelimitedTextNodeConfiguration.DefaultTrailingNewLine))
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
            var raw = column.Value ?? ReadValue(record, column, nodeContext, recordIndex, i);
            values[i] = EnforceStructure(raw, c, nodeContext, recordIndex, i);

            // Checked on the rendered text so that absent, null and empty are one rule, not three.
            if (column.Required && values[i].Length == 0)
            {
                throw MeshAdapterPipelineExecutionException.DelimitedRequiredColumnEmpty(
                    nodeContext, recordIndex, i);
            }
        }

        return string.Join(c.Delimiter, values);
    }

    private static bool ContainsLineBreak(string value) =>
        value.Contains('\r', StringComparison.Ordinal) || value.Contains('\n', StringComparison.Ordinal);

    /// <summary>
    /// A delimiter or a line break inside a value shifts every column after it, and the receiving
    /// side has no quoting convention that could signal the difference. So the value is either
    /// refused or rewritten - it is never passed through as it stands.
    /// </summary>
    private static string EnforceStructure(string value, RenderDelimitedTextNodeConfiguration c,
        INodeContext nodeContext, int recordIndex, int columnIndex)
    {
        if (!value.Contains(c.Delimiter!, StringComparison.Ordinal) && !ContainsLineBreak(value))
        {
            return value;
        }

        var handling = c.OnDelimiterInValue ??
                       RenderDelimitedTextNodeConfiguration.DefaultOnDelimiterInValue;
        if (handling == DelimiterInValueHandling.Fail)
        {
            throw MeshAdapterPipelineExecutionException.DelimitedValueBreaksStructure(
                nodeContext, recordIndex, columnIndex, value);
        }

        var replacement = handling == DelimiterInValueHandling.Replace
            ? c.Replacement ?? string.Empty
            : string.Empty;

        var cleaned = value
            .Replace(c.Delimiter!, replacement, StringComparison.Ordinal)
            .Replace("\r\n", replacement, StringComparison.Ordinal)
            .Replace("\r", replacement, StringComparison.Ordinal)
            .Replace("\n", replacement, StringComparison.Ordinal);

        nodeContext.Warning(
            "RenderDelimitedText: record {0}, column {1} contained the delimiter or a line break; " +
            "the value was rewritten", recordIndex, columnIndex);

        return cleaned;
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
        // A blank target path is not a harmless mistake: the data context treats an empty path as a
        // write to the document root, so the rendered document would replace the whole pipeline
        // data and the chain would carry on without a word. The properties are non-nullable, but a
        // definition carrying an explicit null overwrites the initializer, so the value gets here.
        if (string.IsNullOrWhiteSpace(c.Path))
        {
            throw MeshAdapterPipelineExecutionException.DelimitedPathNotSet(nodeContext, "path");
        }

        if (string.IsNullOrWhiteSpace(c.TargetPath))
        {
            throw MeshAdapterPipelineExecutionException.DelimitedPathNotSet(nodeContext, "targetPath");
        }

        // An out-of-range enum deserializes without complaint and would otherwise land in whichever
        // branch the switch happens to end in - for the handling option that meant silently
        // rewriting values instead of failing on them.
        if (c.LineEnding is { } lineEnding && !Enum.IsDefined(lineEnding))
        {
            throw MeshAdapterPipelineExecutionException.DelimitedOptionUndefined(
                nodeContext, "lineEnding", (int)lineEnding);
        }

        if (c.OnDelimiterInValue is { } handling && !Enum.IsDefined(handling))
        {
            throw MeshAdapterPipelineExecutionException.DelimitedOptionUndefined(
                nodeContext, "onDelimiterInValue", (int)handling);
        }

        if (c.Columns is null || c.Columns.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.DelimitedColumnsNotSet(nodeContext);
        }

        if (string.IsNullOrEmpty(c.Delimiter) || ContainsLineBreak(c.Delimiter))
        {
            throw MeshAdapterPipelineExecutionException.DelimitedDelimiterUnusable(nodeContext, c.Delimiter);
        }

        // Replacing a structural character with another one would only move the problem.
        var replacement = c.Replacement ?? string.Empty;
        if (replacement.Contains(c.Delimiter, StringComparison.Ordinal) || ContainsLineBreak(replacement))
        {
            throw MeshAdapterPipelineExecutionException.DelimitedReplacementUnusable(nodeContext, replacement);
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
