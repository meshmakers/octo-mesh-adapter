using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Turns an array of records into one CSV document: one row per element, one column per configured
/// entry (or per scalar property of the records when none is configured), with an optional header,
/// RFC 4180 quoting, a number and date format per column, an optional byte-order mark and, when
/// asked for, base64 of the UTF-8 bytes so that a file-storing or mail-sending node can take it.
/// </summary>
/// <remarks>
/// Not a replacement of <c>RenderDelimitedText@1</c>: that node renders a fixed layout without any
/// quoting, on purpose. This one writes real CSV.
/// </remarks>
[NodeConfiguration(typeof(ConvertToCsvNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ConvertToCsvNode(NodeDelegate next) : IPipelineNode
{
    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<ConvertToCsvNodeConfiguration>();

        // Configuration mistakes fail whatever else is configured: each would otherwise let a run
        // report success while producing a file nobody can read.
        var plan = Validate(c, nodeContext);

        var sourcePath = JsonNodePath.NormalizePathOrRelative(c.Path);
        var targetPath = JsonNodePath.NormalizePathOrRelative(c.TargetPath);

        // A path that is not an array is a wiring mistake; a silently empty file from a mis-typed
        // path is worse than a loud error.
        if (dataContext.GetKind(sourcePath) != DataKind.Array ||
            dataContext.Get<JsonNode>(sourcePath) is not JsonArray records)
        {
            throw MeshAdapterPipelineExecutionException.CsvSourceNotAnArray(nodeContext, sourcePath);
        }

        for (var i = 0; i < records.Count; i++)
        {
            if (records[i] is not JsonObject)
            {
                throw MeshAdapterPipelineExecutionException.CsvRecordNotAnObject(nodeContext, i);
            }
        }

        var columns = plan.Columns ?? DiscoverColumns(records);
        var delimiter = plan.Delimiter;
        var separator = plan.LineEnding == CsvLineEnding.CrLf ? "\r\n" : "\n";

        var rows = new List<string>(records.Count + 1);

        // The header is written for explicit columns even when there are no records (an empty
        // batch is then a valid, header-only file). Without columns there is nothing to name.
        if (plan.IncludeHeader && columns.Count > 0)
        {
            rows.Add(RenderRow(
                columns.Select(col => new Cell(col.Header, true)).ToList(), plan));
        }

        for (var i = 0; i < records.Count; i++)
        {
            var record = (JsonObject)records[i]!;
            var cells = new List<Cell>(columns.Count);
            for (var j = 0; j < columns.Count; j++)
            {
                cells.Add(ReadCell(record, columns[j], nodeContext, i, j));
            }

            rows.Add(RenderRow(cells, plan));
        }

        var body = string.Join(separator, rows);
        if (rows.Count > 0 && plan.TrailingNewLine)
        {
            body += separator;
        }

        // A byte-order mark in front of an empty document would be a file that is not empty and
        // holds nothing.
        var withMark = plan.ByteOrderMark && body.Length > 0;
        var utf8Length = Encoding.UTF8.GetByteCount(body) + (withMark ? Utf8Bom.Length : 0);

        if (plan.OutputAsBase64)
        {
            var bytes = new byte[utf8Length];
            var offset = 0;
            if (withMark)
            {
                Utf8Bom.CopyTo(bytes, 0);
                offset = Utf8Bom.Length;
            }

            Encoding.UTF8.GetBytes(body, 0, body.Length, bytes, offset);
            dataContext.Set(targetPath, Convert.ToBase64String(bytes),
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        }
        else
        {
            dataContext.Set(targetPath, withMark ? "﻿" + body : body,
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        }

        if (!string.IsNullOrWhiteSpace(c.ContentLengthTargetPath))
        {
            dataContext.Set(JsonNodePath.NormalizePathOrRelative(c.ContentLengthTargetPath), (long)utf8Length,
                c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);
        }

        nodeContext.Info("ConvertToCsv: {0} record(s), {1} column(s), {2} byte(s)",
            records.Count, columns.Count, utf8Length);

        await next(dataContext, nodeContext);
    }

    // ---------- Rendering ----------

    private readonly record struct Cell(string Text, bool IsText);

    private static string RenderRow(List<Cell> cells, Plan plan)
    {
        var sb = new StringBuilder();
        for (var i = 0; i < cells.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(plan.Delimiter);
            }

            sb.Append(Quote(cells[i], plan));
        }

        if (plan.TrailingDelimiter)
        {
            sb.Append(plan.Delimiter);
        }

        return sb.ToString();
    }

    private static string Quote(Cell cell, Plan plan)
    {
        if (cell.Text.Length == 0)
        {
            return string.Empty;
        }

        var needs = cell.Text.Contains(plan.Delimiter, StringComparison.Ordinal) ||
                    cell.Text.Contains('"', StringComparison.Ordinal) ||
                    cell.Text.Contains('\r', StringComparison.Ordinal) ||
                    cell.Text.Contains('\n', StringComparison.Ordinal);

        var quote = needs || plan.Quoting switch
        {
            CsvQuoting.All => true,
            CsvQuoting.Text => cell.IsText,
            _ => false
        };

        return quote ? "\"" + cell.Text.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"" : cell.Text;
    }

    // ---------- Columns ----------

    private sealed record ResolvedColumn(
        string Header,
        string? Key,
        string? Path,
        string? Constant,
        CsvValueType Type,
        string? Format,
        int? Decimals,
        decimal Factor,
        TimeZoneInfo? TimeZone,
        bool Required);

    /// <summary>
    /// Takes every scalar property of the records, in the order they first appear. A nested object
    /// or array is left out: it has no single text.
    /// </summary>
    private static List<ResolvedColumn> DiscoverColumns(JsonArray records)
    {
        var keys = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var node in records)
        {
            foreach (var pair in (JsonObject)node!)
            {
                if (pair.Value is JsonObject or JsonArray || !seen.Add(pair.Key))
                {
                    continue;
                }

                keys.Add(pair.Key);
            }
        }

        return keys
            .Select(k => new ResolvedColumn(k, k, null, null, CsvValueType.Auto, null, null, 1m, null, false))
            .ToList();
    }

    private static Cell ReadCell(JsonObject record, ResolvedColumn column, INodeContext nodeContext,
        int recordIndex, int columnIndex)
    {
        string text;
        bool isText;

        if (column.Constant is not null)
        {
            (text, isText) = (column.Constant, true);
        }
        else if (column.Key is null && column.Path is null)
        {
            (text, isText) = (string.Empty, true);
        }
        else
        {
            var node = column.Key is not null
                ? (record.TryGetPropertyValue(column.Key, out var direct) ? direct : null)
                : JsonNodePath.Select(record, column.Path!);
            (text, isText) = ConvertValue(node, column, nodeContext, recordIndex, columnIndex);
        }

        if (column.Required && text.Length == 0)
        {
            throw MeshAdapterPipelineExecutionException.CsvValueInvalid(nodeContext, recordIndex, columnIndex,
                "the column is required but rendered empty.");
        }

        return new Cell(text, isText);
    }

    private static (string Text, bool IsText) ConvertValue(JsonNode? node, ResolvedColumn column,
        INodeContext nodeContext, int recordIndex, int columnIndex)
    {
        if (node is null)
        {
            return (string.Empty, true);
        }

        if (node is JsonObject or JsonArray)
        {
            throw MeshAdapterPipelineExecutionException.CsvValueInvalid(nodeContext, recordIndex, columnIndex,
                "the value is an object or array, which cannot be a cell.");
        }

        var value = (JsonValue)node;
        var kind = value.GetValueKind();
        if (kind == JsonValueKind.Null)
        {
            return (string.Empty, true);
        }

        switch (column.Type)
        {
            case CsvValueType.String:
                return (ScalarText(value, kind), true);

            case CsvValueType.Boolean:
                return (ReadBoolean(value, kind, nodeContext, recordIndex, columnIndex), false);

            case CsvValueType.Number:
                return (ReadNumber(value, kind, column, nodeContext, recordIndex, columnIndex), false);

            case CsvValueType.DateTime:
                return (ReadDateTime(value, kind, column, nodeContext, recordIndex, columnIndex), true);

            default: // Auto
                return kind switch
                {
                    JsonValueKind.Number => (ReadNumber(value, kind, column, nodeContext, recordIndex, columnIndex), false),
                    JsonValueKind.True or JsonValueKind.False => (kind == JsonValueKind.True ? "true" : "false", false),
                    _ => (ScalarText(value, kind), true)
                };
        }
    }

    private static string ScalarText(JsonValue value, JsonValueKind kind) => kind switch
    {
        JsonValueKind.String => value.GetValue<string>(),
        JsonValueKind.True => "true",
        JsonValueKind.False => "false",
        _ => value.ToJsonString() // a number keeps its token
    };

    private static string ReadBoolean(JsonValue value, JsonValueKind kind, INodeContext nodeContext,
        int recordIndex, int columnIndex)
    {
        if (kind is JsonValueKind.True or JsonValueKind.False)
        {
            return kind == JsonValueKind.True ? "true" : "false";
        }

        if (kind == JsonValueKind.String && bool.TryParse(value.GetValue<string>(), out var parsed))
        {
            return parsed ? "true" : "false";
        }

        throw MeshAdapterPipelineExecutionException.CsvValueInvalid(nodeContext, recordIndex, columnIndex,
            "the value is not a boolean.");
    }

    private static string ReadNumber(JsonValue value, JsonValueKind kind, ResolvedColumn column,
        INodeContext nodeContext, int recordIndex, int columnIndex)
    {
        var plain = column.Format is null && column.Decimals is null && column.Factor == 1m;
        var token = kind == JsonValueKind.String ? value.GetValue<string>() : value.ToJsonString();

        // Without a format a number keeps its token, so 0.50 stays 0.50 and no culture reshapes it.
        if (plain && kind == JsonValueKind.Number)
        {
            return token;
        }

        if (kind is not (JsonValueKind.Number or JsonValueKind.String) ||
            !decimal.TryParse(token, NumberStyles.Float, CultureInfo.InvariantCulture, out var number))
        {
            throw MeshAdapterPipelineExecutionException.CsvValueInvalid(nodeContext, recordIndex, columnIndex,
                "the value is not a number the cell can hold.");
        }

        try
        {
            number *= column.Factor;
            if (column.Decimals is { } decimals)
            {
                number = Math.Round(number, decimals, MidpointRounding.AwayFromZero);
            }
        }
        catch (OverflowException)
        {
            throw MeshAdapterPipelineExecutionException.CsvValueInvalid(nodeContext, recordIndex, columnIndex,
                "the number is too large after applying the factor.");
        }

        // A zero that came out of rounding a small negative would print as "-0".
        if (number == 0m)
        {
            number = 0m;
        }

        return column.Format is null
            ? number.ToString(CultureInfo.InvariantCulture)
            : number.ToString(column.Format, CultureInfo.InvariantCulture);
    }

    private static string ReadDateTime(JsonValue value, JsonValueKind kind, ResolvedColumn column,
        INodeContext nodeContext, int recordIndex, int columnIndex)
    {
        // The stored value arrives as text. One without an offset is read as UTC, the way the
        // stream-data nodes read one, so the host's own zone never shifts a point in time.
        if (kind != JsonValueKind.String ||
            !DateTimeOffset.TryParse(value.GetValue<string>(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            throw MeshAdapterPipelineExecutionException.CsvValueInvalid(nodeContext, recordIndex, columnIndex,
                "the value is not a date and time.");
        }

        var local = TimeZoneInfo.ConvertTime(parsed, column.TimeZone ?? TimeZoneInfo.Utc);
        return local.ToString(column.Format ?? "s", CultureInfo.InvariantCulture);
    }

    // ---------- Validation ----------

    private sealed record Plan(
        string Delimiter,
        CsvLineEnding LineEnding,
        bool IncludeHeader,
        CsvQuoting Quoting,
        bool TrailingDelimiter,
        bool TrailingNewLine,
        bool ByteOrderMark,
        bool OutputAsBase64,
        List<ResolvedColumn>? Columns);

    private static Plan Validate(ConvertToCsvNodeConfiguration c, INodeContext nodeContext)
    {
        // A blank target path is not harmless: the data context reads an empty path as the document
        // ROOT, so the file would replace the whole pipeline data and the chain would carry on.
        if (string.IsNullOrWhiteSpace(c.Path))
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                "'path' must be a JSONPath; an empty one would address the document root.");
        }

        if (string.IsNullOrWhiteSpace(c.TargetPath))
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                "'targetPath' must be a JSONPath; an empty one would address the document root.");
        }

        ValidateSinglePath(JsonNodePath.NormalizePathOrRelative(c.Path), "path", nodeContext);
        ValidateSinglePath(JsonNodePath.NormalizePathOrRelative(c.TargetPath), "targetPath", nodeContext);
        if (!string.IsNullOrWhiteSpace(c.ContentLengthTargetPath))
        {
            ValidateSinglePath(JsonNodePath.NormalizePathOrRelative(c.ContentLengthTargetPath),
                "contentLengthTargetPath", nodeContext);
        }

        // Out-of-range enums deserialize without complaint and would otherwise land in whichever
        // branch a switch happens to end in.
        RequireDefined(c.LineEnding, "lineEnding", nodeContext);
        RequireDefined(c.Quoting, "quoting", nodeContext);

        // Exactly one character, and never one that is part of the quoting syntax: a double quote
        // as delimiter cannot be told from the quoting.
        var delimiter = c.Delimiter ?? ConvertToCsvNodeConfiguration.DefaultDelimiter;
        if (delimiter.Length != 1 || delimiter is "\r" or "\n" or "\"")
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"delimiter '{Escape(delimiter)}' must be exactly one character and neither a line break nor a double quote.");
        }

        List<ResolvedColumn>? columns = null;
        if (c.Columns is not null)
        {
            columns = [];
            var index = 0;
            foreach (var column in c.Columns)
            {
                columns.Add(ResolveColumn(column, index, nodeContext));
                index++;
            }
        }

        return new Plan(
            delimiter,
            c.LineEnding ?? ConvertToCsvNodeConfiguration.DefaultLineEnding,
            c.IncludeHeader ?? ConvertToCsvNodeConfiguration.DefaultIncludeHeader,
            c.Quoting ?? ConvertToCsvNodeConfiguration.DefaultQuoting,
            c.TrailingDelimiter ?? ConvertToCsvNodeConfiguration.DefaultTrailingDelimiter,
            c.TrailingNewLine ?? ConvertToCsvNodeConfiguration.DefaultTrailingNewLine,
            c.ByteOrderMark ?? ConvertToCsvNodeConfiguration.DefaultByteOrderMark,
            c.OutputAsBase64 ?? ConvertToCsvNodeConfiguration.DefaultOutputAsBase64,
            columns);
    }

    private static ResolvedColumn ResolveColumn(CsvColumn? column, int index, INodeContext nodeContext)
    {
        if (column is null)
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext, $"column {index} is null.");
        }

        if (column.Value is not null && !string.IsNullOrEmpty(column.ValuePath))
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"column {index} sets both 'value' and 'valuePath'.");
        }

        RequireDefined(column.DataType, $"column {index} dataType", nodeContext);
        var type = column.DataType ?? CsvValueType.Auto;

        if (column.Value is not null &&
            (column.Format is not null || column.Decimals is not null || column.Factor is not null ||
             column.TimeZone is not null || column.DataType is not null && column.DataType != CsvValueType.String))
        {
            // A constant is text; a format next to it would be ignored without a word.
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"column {index} is a constant, so 'format', 'decimals', 'factor', 'timeZone' and a type other than String have no effect.");
        }

        if (column.Decimals is < 0 or > 28)
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"column {index}: 'decimals' must be between 0 and 28.");
        }

        if ((column.Decimals is not null || column.Factor is not null) && type is not (CsvValueType.Auto or CsvValueType.Number))
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"column {index}: 'decimals' and 'factor' apply to numbers only.");
        }

        TimeZoneInfo? zone = null;
        if (!string.IsNullOrWhiteSpace(column.TimeZone))
        {
            if (type != CsvValueType.DateTime)
            {
                throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                    $"column {index}: 'timeZone' applies to a DateTime column only.");
            }

            try
            {
                zone = TimeZoneInfo.FindSystemTimeZoneById(column.TimeZone);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                    $"column {index}: '{column.TimeZone}' is not a known time zone.");
            }
        }

        string? path = null;
        if (!string.IsNullOrEmpty(column.ValuePath))
        {
            path = JsonNodePath.NormalizePathOrRelative(column.ValuePath);
            ValidateSinglePath(path, $"column {index}", nodeContext);
        }

        var header = column.Header ?? (path is null ? string.Empty : LastName(path));
        return new ResolvedColumn(header, null, path, column.Value, type, column.Format, column.Decimals,
            column.Factor ?? 1m, zone, column.Required);
    }

    /// <summary>The last property name of a path (<c>$.customer.name</c> gives <c>name</c>).</summary>
    private static string LastName(string path)
    {
        var expression = JsonPathParser.Parse(path);
        var last = expression.Segments.OfType<PropertySegment>().LastOrDefault();
        return last?.Name ?? string.Empty;
    }

    /// <summary>
    /// Parsed once here rather than per record: a malformed path would otherwise surface from deep
    /// inside the walk. A path that selects a SET is refused rather than resolved to whichever
    /// member comes first - a cell holds one value.
    /// </summary>
    private static void ValidateSinglePath(string path, string location, INodeContext nodeContext)
    {
        JsonPathExpression expression;
        try
        {
            expression = JsonPathParser.Parse(path);
        }
        catch (Exception ex) when (ex is not PipelineExecutionException)
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"{location}: '{path}' is not a valid JSONPath.", ex);
        }

        if (expression.Segments.Any(s => s is WildcardSegment or RecursiveDescentSegment or FilterSegment))
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"{location}: '{path}' selects a set of values; wildcard, filter and recursive descent are not accepted.");
        }
    }

    private static void RequireDefined<TEnum>(TEnum? value, string property, INodeContext nodeContext)
        where TEnum : struct, Enum
    {
        if (value is { } v && !Enum.IsDefined(v))
        {
            throw MeshAdapterPipelineExecutionException.CsvConfigurationInvalid(nodeContext,
                $"'{property}' has the undefined value {Convert.ToInt32(v, CultureInfo.InvariantCulture)}.");
        }
    }

    private static string Escape(string value) =>
        value.Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal);
}
