using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// How a value that contains the delimiter or a line break is handled.
/// </summary>
public enum DelimiterInValueHandling
{
    /// <summary>Fail the node, naming the record and the column.</summary>
    Fail,

    /// <summary>Substitute <see cref="RenderDelimitedTextNodeConfiguration.Replacement" /> and warn.</summary>
    Replace,

    /// <summary>Remove the offending characters and warn.</summary>
    Strip
}

/// <summary>
/// Record separator written between rendered rows.
/// </summary>
public enum DelimitedLineEnding
{
    /// <summary>Line feed only.</summary>
    Lf,

    /// <summary>Carriage return followed by line feed.</summary>
    CrLf
}

/// <summary>
/// One output column: a constant, a value read from the record, or an empty column. A column with
/// neither <see cref="Value" /> nor <see cref="ValuePath" /> renders empty - that is how a fixed
/// layout expresses a reserved field.
/// </summary>
// ReSharper disable once ClassNeverInstantiated.Global
public record DelimitedColumn
{
    /// <summary>Constant text. Mutually exclusive with <see cref="ValuePath" />.</summary>
    public string? Value { get; set; }

    /// <summary>JSONPath relative to the record. Mutually exclusive with <see cref="Value" />.</summary>
    public string? ValuePath { get; set; }

    /// <summary>
    /// When set, an empty rendered value fails the node instead of writing an empty column. Left
    /// unset a column is optional, which is what a fixed layout needs: most of its columns are
    /// reserved and carry nothing on most records.
    /// </summary>
    public bool Required { get; set; }
}

/// <summary>
/// Configuration for rendering an array of records into one delimited-text document.
/// </summary>
[NodeName("RenderDelimitedText", 1)]
public record RenderDelimitedTextNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>Text written between columns. Must be non-empty and must not contain CR or LF.</summary>
    [PropertyGroup("Options", 0)]
    public string? Delimiter { get; set; } = "|";

    /// <summary>
    /// Record separator, <see cref="DelimitedLineEnding.Lf" /> when unset. Never derived from the
    /// operating system.
    /// </summary>
    [PropertyGroup("Options", 1)]
    public DelimitedLineEnding? LineEnding { get; set; }

    /// <summary>
    /// Whether a final record separator is appended after the last row; true when unset.
    /// </summary>
    [PropertyGroup("Options", 2)]
    public bool? TrailingNewLine { get; set; }

    /// <summary>
    /// How a value containing the delimiter, CR or LF is handled;
    /// <see cref="DelimiterInValueHandling.Fail" /> when unset.
    /// </summary>
    [PropertyGroup("Options", 3)]
    public DelimiterInValueHandling? OnDelimiterInValue { get; set; }

    /// <summary>
    /// Substitute used by <see cref="DelimiterInValueHandling.Replace" />. Defaults to the empty
    /// string, which makes Replace behave as Strip.
    /// </summary>
    [PropertyGroup("Options", 4)]
    public string? Replacement { get; set; } = string.Empty;

    /// <summary>Output columns, in order. One entry per column of the target layout.</summary>
    // ReSharper disable once CollectionNeverUpdated.Global
    [PropertyGroup("Data Mapping", 0)]
    public ICollection<DelimitedColumn>? Columns { get; set; }

    /// <summary>Default record separator when <see cref="LineEnding" /> is unset.</summary>
    public const DelimitedLineEnding DefaultLineEnding = DelimitedLineEnding.Lf;

    /// <summary>Default for <see cref="TrailingNewLine" /> when unset.</summary>
    public const bool DefaultTrailingNewLine = true;

    /// <summary>Default handling when <see cref="OnDelimiterInValue" /> is unset.</summary>
    public const DelimiterInValueHandling DefaultOnDelimiterInValue = DelimiterInValueHandling.Fail;
}
