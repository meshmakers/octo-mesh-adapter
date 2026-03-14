using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for importing data from CSV files
/// </summary>
[NodeName("ImportFromCsv", 1)]
public record ImportFromCsvNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Index of the file in $.files[] array (set by FromHttpRequest@1 for multipart/form-data uploads)
    /// </summary>
    public int FileIndex { get; set; }

    /// <summary>
    /// Column delimiter character
    /// </summary>
    public string Delimiter { get; set; } = ";";

    /// <summary>
    /// File encoding (e.g. utf-8, utf-16le)
    /// </summary>
    public string Encoding { get; set; } = "utf-8";

    /// <summary>
    /// Whether the first data row contains column headers
    /// </summary>
    public bool HasHeaderRow { get; set; } = true;

    /// <summary>
    /// Number of rows to skip before the header/data rows
    /// </summary>
    public int SkipRows { get; set; }

    /// <summary>
    /// Column-to-property mappings
    /// </summary>
    public required ICollection<CsvColumnMapping> ColumnMappings { get; set; }
}

/// <summary>
/// Maps a CSV column to a JSON output property with type conversion
/// </summary>
public record CsvColumnMapping
{
    /// <summary>
    /// Source column name (matched against header row)
    /// </summary>
    public string? SourceColumn { get; set; }

    /// <summary>
    /// Source column index (alternative to SourceColumn, zero-based)
    /// </summary>
    public int? SourceIndex { get; set; }

    /// <summary>
    /// Target JSON property name in the output object
    /// </summary>
    public required string TargetProperty { get; set; }

    /// <summary>
    /// Data type for value conversion
    /// </summary>
    public CsvDataType DataType { get; set; } = CsvDataType.String;

    /// <summary>
    /// Date format string for DateTime parsing (e.g. "dd.MM.yyyy")
    /// </summary>
    public string? DateFormat { get; set; }

    /// <summary>
    /// Culture name for number parsing (e.g. "de-AT")
    /// </summary>
    public string? NumberCulture { get; set; }
}

/// <summary>
/// Supported CSV data types for column value conversion
/// </summary>
public enum CsvDataType
{
    /// <summary>String value (trimmed, empty becomes null)</summary>
    String,

    /// <summary>Integer value</summary>
    Int,

    /// <summary>Double value (supports culture-specific formats)</summary>
    Double,

    /// <summary>Boolean value (supports "0"/"1" and "true"/"false")</summary>
    Boolean,

    /// <summary>DateTime value (parsed with optional format string, output as ISO 8601)</summary>
    DateTime
}
