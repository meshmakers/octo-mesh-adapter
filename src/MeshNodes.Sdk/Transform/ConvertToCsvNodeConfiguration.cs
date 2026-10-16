using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Which values of the document are put in double quotes.
/// </summary>
public enum CsvQuoting
{
    /// <summary>
    /// Only a value that needs it (contains the delimiter, a double quote, CR or LF) is quoted, as
    /// RFC 4180 describes.
    /// </summary>
    Minimal,

    /// <summary>
    /// Every text value is quoted: strings, formatted dates and the header cells. Numbers and
    /// booleans stay bare, empty cells stay empty. A value that needs quoting is quoted as well.
    /// </summary>
    Text,

    /// <summary>Every non-empty cell is quoted, whatever its type.</summary>
    All
}

/// <summary>
/// Record separator written between rows.
/// </summary>
public enum CsvLineEnding
{
    /// <summary>Carriage return followed by line feed, the RFC 4180 separator.</summary>
    CrLf,

    /// <summary>Line feed only.</summary>
    Lf
}

/// <summary>
/// How the value of a column is read from the record and turned into text.
/// </summary>
public enum CsvValueType
{
    /// <summary>
    /// Follows the value: a string stays text, a number keeps its JSON token (or is formatted when
    /// <see cref="CsvColumn.Format" /> / <see cref="CsvColumn.Decimals" /> / <see cref="CsvColumn.Factor" />
    /// is set), a boolean is written as <c>true</c> / <c>false</c>.
    /// </summary>
    Auto,

    /// <summary>Text. A number or boolean in the record is written as its text.</summary>
    String,

    /// <summary>A number. A string that is a valid number is accepted; anything else fails the node.</summary>
    Number,

    /// <summary>A boolean, written as <c>true</c> / <c>false</c>.</summary>
    Boolean,

    /// <summary>
    /// A point in time, given as an ISO 8601 string. A value without an offset is read as UTC, the
    /// way the stream-data nodes read one. It is converted into <see cref="CsvColumn.TimeZone" />
    /// and written with <see cref="CsvColumn.Format" />.
    /// </summary>
    DateTime
}

/// <summary>
/// One output column: a value read from the record, a constant, or an empty column.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record CsvColumn
{
    /// <summary>Text of the header cell. Defaults to the last name of <see cref="ValuePath" />.</summary>
    public string? Header { get; set; }

    /// <summary>JSONPath relative to the record. Mutually exclusive with <see cref="Value" />.</summary>
    public string? ValuePath { get; set; }

    /// <summary>Constant text written in every row. Mutually exclusive with <see cref="ValuePath" />.</summary>
    public string? Value { get; set; }

    /// <summary>How the value is read and written; <see cref="CsvValueType.Auto" /> when unset.</summary>
    public CsvValueType? DataType { get; set; }

    /// <summary>
    /// .NET format string. For a number a custom or standard numeric format (<c>0.###</c>,
    /// <c>0.000</c>, <c>F2</c>), for a date a custom or standard date format (<c>dd.MM.yyyy HH:mm</c>).
    /// Always applied with the invariant culture, so the decimal separator is a point.
    /// </summary>
    public string? Format { get; set; }

    /// <summary>
    /// Number of decimals a number is rounded to, half away from zero, before it is formatted.
    /// Unset leaves the value as it is.
    /// </summary>
    public int? Decimals { get; set; }

    /// <summary>
    /// Factor a number is multiplied with before it is rounded and formatted (unit conversion,
    /// e.g. <c>4</c> to turn the energy of a quarter hour into power). Unset is 1.
    /// </summary>
    public decimal? Factor { get; set; }

    /// <summary>
    /// IANA id of the time zone a date is converted into before it is formatted (for example
    /// <c>Europe/Vienna</c>). Unset is UTC.
    /// </summary>
    public string? TimeZone { get; set; }

    /// <summary>
    /// When set, an empty rendered value fails the node instead of writing an empty cell.
    /// </summary>
    public bool Required { get; set; }
}

/// <summary>
/// Configuration for turning an array of records into one CSV document.
/// </summary>
[NodeName("ConvertToCsv", 1)]
public record ConvertToCsvNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>Exactly one character between cells. Defaults to <c>;</c>.</summary>
    [PropertyGroup("Options", 0)]
    public string? Delimiter { get; set; }

    /// <summary>Record separator, <see cref="CsvLineEnding.CrLf" /> when unset.</summary>
    [PropertyGroup("Options", 1)]
    public CsvLineEnding? LineEnding { get; set; }

    /// <summary>Whether a header row is written first; true when unset.</summary>
    [PropertyGroup("Options", 2)]
    public bool? IncludeHeader { get; set; }

    /// <summary>Which cells are quoted, <see cref="CsvQuoting.Minimal" /> when unset.</summary>
    [PropertyGroup("Options", 3)]
    public CsvQuoting? Quoting { get; set; }

    /// <summary>
    /// Whether every row, the header included, ends with a delimiter, which some fixed formats
    /// expect; false when unset.
    /// </summary>
    [PropertyGroup("Options", 4)]
    public bool? TrailingDelimiter { get; set; }

    /// <summary>
    /// Whether a line end follows the last row; true when unset. An empty array writes an empty
    /// document either way.
    /// </summary>
    [PropertyGroup("Options", 5)]
    public bool? TrailingNewLine { get; set; }

    /// <summary>
    /// Whether the document starts with a UTF-8 byte-order mark, which Excel needs to read a UTF-8
    /// file as UTF-8; false when unset.
    /// </summary>
    [PropertyGroup("Output", 0)]
    public bool? ByteOrderMark { get; set; }

    /// <summary>
    /// Whether the document is written as base64 of its UTF-8 bytes (the byte-order mark included
    /// when set) instead of as text, which is what a node that stores or attaches a file takes;
    /// false when unset.
    /// </summary>
    [PropertyGroup("Output", 1)]
    public bool? OutputAsBase64 { get; set; }

    /// <summary>
    /// Optional path to write the byte length of the UTF-8 document to (a long), for a following
    /// node that needs the length explicitly.
    /// </summary>
    [PropertyGroup("Output", 2, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }

    /// <summary>
    /// Output columns, in order. Unset takes every scalar property of the records, in the order
    /// they first appear, with the property name as header.
    /// </summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    [PropertyGroup("Data Mapping", 0)]
    public ICollection<CsvColumn>? Columns { get; set; }

    /// <summary>Default for <see cref="Delimiter" />.</summary>
    public const string DefaultDelimiter = ";";

    /// <summary>Default for <see cref="LineEnding" />.</summary>
    public const CsvLineEnding DefaultLineEnding = CsvLineEnding.CrLf;

    /// <summary>Default for <see cref="IncludeHeader" />.</summary>
    public const bool DefaultIncludeHeader = true;

    /// <summary>Default for <see cref="Quoting" />.</summary>
    public const CsvQuoting DefaultQuoting = CsvQuoting.Minimal;

    /// <summary>Default for <see cref="TrailingDelimiter" />.</summary>
    public const bool DefaultTrailingDelimiter = false;

    /// <summary>Default for <see cref="TrailingNewLine" />.</summary>
    public const bool DefaultTrailingNewLine = true;

    /// <summary>Default for <see cref="ByteOrderMark" />.</summary>
    public const bool DefaultByteOrderMark = false;

    /// <summary>Default for <see cref="OutputAsBase64" />.</summary>
    public const bool DefaultOutputAsBase64 = false;
}
