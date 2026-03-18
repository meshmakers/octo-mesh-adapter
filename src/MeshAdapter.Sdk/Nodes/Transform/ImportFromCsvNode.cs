using System.Globalization;
using System.Text;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that imports CSV file data into a JArray of JObjects based on column mappings
/// </summary>
[NodeConfiguration(typeof(ImportFromCsvNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ImportFromCsvNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<ImportFromCsvNodeConfiguration>();

        var fileData = GetFileData(dataContext, config, nodeContext);
        if (fileData == null)
        {
            return;
        }

        var content = DecodeFileContent(fileData, config, nodeContext);
        if (content == null)
        {
            return;
        }

        var lines = SplitLines(content);
        if (lines.Count == 0)
        {
            nodeContext.Warning("CSV file contains no data lines");
            dataContext.SetValueByPath(config.TargetPath, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, new JArray());
            await next(dataContext, nodeContext);
            return;
        }

        // Skip configured rows
        var dataStartIndex = config.SkipRows;
        if (dataStartIndex >= lines.Count)
        {
            nodeContext.Warning($"SkipRows ({config.SkipRows}) exceeds total lines ({lines.Count})");
            dataContext.SetValueByPath(config.TargetPath, config.DocumentMode,
                config.TargetValueKind, config.TargetValueWriteMode, new JArray());
            await next(dataContext, nodeContext);
            return;
        }

        // Parse header row
        string[]? headers = null;
        if (config.HasHeaderRow)
        {
            headers = ParseCsvLine(lines[dataStartIndex], config.Delimiter);
            dataStartIndex++;
        }

        // Build column index lookup from mappings
        var resolvedMappings = ResolveMappings(config.ColumnMappings, headers, nodeContext);

        // Parse data rows
        var result = new JArray();
        for (var i = dataStartIndex; i < lines.Count; i++)
        {
            var line = lines[i];
            if (string.IsNullOrWhiteSpace(line))
            {
                continue;
            }

            var fields = ParseCsvLine(line, config.Delimiter);
            var row = new JObject();

            foreach (var (mapping, columnIndex) in resolvedMappings)
            {
                if (columnIndex < 0 || columnIndex >= fields.Length)
                {
                    continue;
                }

                var rawValue = fields[columnIndex].Trim();
                var convertedValue = ConvertValue(rawValue, mapping, nodeContext, i + 1);
                if (convertedValue != null)
                {
                    row[mapping.TargetProperty] = convertedValue;
                }
            }

            result.Add(row);
        }

        nodeContext.Info($"Parsed {result.Count} rows from CSV file");

        dataContext.SetValueByPath(config.TargetPath, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode, result);

        await next(dataContext, nodeContext);
    }

    private static string? GetFileData(IDataContext dataContext, ImportFromCsvNodeConfiguration config,
        INodeContext nodeContext)
    {
        var filesToken = dataContext.Current?.SelectToken($"$.files[{config.FileIndex}].data");
        if (filesToken == null)
        {
            nodeContext.Error($"No file found at $.files[{config.FileIndex}].data");
            return null;
        }

        return filesToken.Value<string>();
    }

    internal static string? DecodeFileContent(string base64Data, ImportFromCsvNodeConfiguration config,
        INodeContext nodeContext)
    {
        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64Data);
        }
        catch (FormatException ex)
        {
            nodeContext.Error($"Invalid base64 data: {ex.Message}");
            return null;
        }

        var encoding = GetEncoding(config.Encoding);

        var content = encoding.GetString(bytes);

        // Strip BOM if present
        if (content.Length > 0 && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        return content;
    }

    private static Encoding GetEncoding(string encodingName)
    {
        return encodingName.ToLowerInvariant() switch
        {
            "utf-16le" or "utf-16" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "utf-32" => Encoding.UTF32,
            "ascii" => Encoding.ASCII,
            "latin1" or "iso-8859-1" => Encoding.Latin1,
            _ => Encoding.UTF8
        };
    }

    internal static List<string> SplitLines(string content)
    {
        var lines = new List<string>();
        using var reader = new StringReader(content);
        while (reader.ReadLine() is { } line)
        {
            if (!string.IsNullOrWhiteSpace(line))
            {
                lines.Add(line);
            }
        }

        return lines;
    }

    internal static string[] ParseCsvLine(string line, string delimiter)
    {
        var fields = new List<string>();
        var current = new StringBuilder();
        var inQuotes = false;
        var delimChar = delimiter.Length > 0 ? delimiter[0] : ';';

        for (var i = 0; i < line.Length; i++)
        {
            var c = line[i];

            if (inQuotes)
            {
                if (c == '"')
                {
                    // Check for escaped quote ("")
                    if (i + 1 < line.Length && line[i + 1] == '"')
                    {
                        current.Append('"');
                        i++; // skip next quote
                    }
                    else
                    {
                        inQuotes = false;
                    }
                }
                else
                {
                    current.Append(c);
                }
            }
            else
            {
                if (c == '"')
                {
                    inQuotes = true;
                }
                else if (c == delimChar)
                {
                    fields.Add(current.ToString());
                    current.Clear();
                }
                else
                {
                    current.Append(c);
                }
            }
        }

        fields.Add(current.ToString());
        return fields.ToArray();
    }

    private static List<(CsvColumnMapping Mapping, int ColumnIndex)> ResolveMappings(
        ICollection<CsvColumnMapping> mappings, string[]? headers, INodeContext nodeContext)
    {
        var resolved = new List<(CsvColumnMapping, int)>();

        foreach (var mapping in mappings)
        {
            int columnIndex;

            if (mapping.SourceIndex.HasValue)
            {
                columnIndex = mapping.SourceIndex.Value;
            }
            else if (mapping.SourceColumn != null && headers != null)
            {
                columnIndex = Array.FindIndex(headers, h => h.Trim() == mapping.SourceColumn);
                if (columnIndex < 0)
                {
                    nodeContext.Warning(
                        $"Column '{mapping.SourceColumn}' not found in CSV headers for target '{mapping.TargetProperty}'");
                    continue;
                }
            }
            else
            {
                nodeContext.Warning(
                    $"No source column or index specified for target '{mapping.TargetProperty}'");
                continue;
            }

            resolved.Add((mapping, columnIndex));
        }

        return resolved;
    }

    internal static JToken? ConvertValue(string rawValue, CsvColumnMapping mapping, INodeContext nodeContext,
        int lineNumber)
    {
        if (string.IsNullOrEmpty(rawValue))
        {
            return null;
        }

        try
        {
            return mapping.DataType switch
            {
                CsvDataType.String => rawValue,
                CsvDataType.Int => int.Parse(rawValue, CultureInfo.InvariantCulture),
                CsvDataType.Double => ParseDouble(rawValue, mapping.NumberCulture),
                CsvDataType.Boolean => ParseBoolean(rawValue),
                CsvDataType.DateTime => ParseDateTime(rawValue, mapping.DateFormat),
                _ => rawValue
            };
        }
        catch (Exception ex)
        {
            nodeContext.Warning(
                $"Failed to convert value '{rawValue}' to {mapping.DataType} for '{mapping.TargetProperty}' at line {lineNumber}: {ex.Message}");
            return null;
        }
    }

    private static double ParseDouble(string value, string? cultureName)
    {
        var culture = string.IsNullOrEmpty(cultureName)
            ? CultureInfo.InvariantCulture
            : CultureInfo.GetCultureInfo(cultureName);

        var numberFormat = culture.NumberFormat;
        var decimalSeparator = numberFormat.NumberDecimalSeparator;
        var groupSeparator = numberFormat.NumberGroupSeparator;

        // Normalize common thousands separators (e.g. '.' in CSV data) to the culture's
        // actual group separator. ICU updates may change the group separator (e.g. from '.'
        // to narrow no-break space for de-AT), but real-world CSV files still use '.'.
        if (decimalSeparator == "," && value.Contains('.') && groupSeparator != ".")
        {
            value = value.Replace(".", groupSeparator);
        }

        return double.Parse(value, NumberStyles.Number, culture);
    }

    private static bool ParseBoolean(string value)
    {
        return value switch
        {
            "1" => true,
            "0" => false,
            _ => bool.Parse(value)
        };
    }

    private static DateTime ParseDateTime(string value, string? format)
    {
        if (!string.IsNullOrEmpty(format))
        {
            return DateTime.ParseExact(value, format, CultureInfo.InvariantCulture, DateTimeStyles.None);
        }

        return DateTime.Parse(value, CultureInfo.InvariantCulture);
    }
}
