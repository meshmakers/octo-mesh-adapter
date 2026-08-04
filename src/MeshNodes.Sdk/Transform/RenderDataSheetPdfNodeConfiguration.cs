using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for <c>RenderDataSheetPdf@1</c>. Renders a structured data
/// sheet (title, subtitle, labelled sections and an optional footer note) into
/// a single-page PDF using QuestPDF. Generic: the domain knowledge lives in the
/// model built by the pipeline, not in the node.
/// <para>
/// The value at <see cref="SourceTargetPathNodeConfiguration.Path"/> must be a
/// JSON object of the shape:
/// <code>
/// {
///   "title": "Cover sheet",
///   "subtitle": "RE-2025-001",
///   "culture": "de-AT",
///   "numberDecimalSeparator": ",",
///   "numberGroupSeparator": ".",
///   "sections": [
///     { "heading": "Document", "rows": [
///       { "label": "Number", "value": "RE-2025-001" },
///       { "label": "Gross", "value": 1186.96, "format": "N2", "suffix": "EUR" }
///     ] }
///   ],
///   "footerHeading": "Note to tax advisor",
///   "footerText": "Please book against travel expenses."
/// }
/// </code>
/// Keys are matched case-insensitively. A row's optional <c>format</c> is a
/// .NET numeric format string applied when the row value is numeric — e.g.
/// rendering <c>1186.96</c> as <c>1.186,96</c> so document-recognition
/// software (BMD) parses it as an amount. The number format comes from the
/// model's optional <c>culture</c> (default invariant), optionally overridden
/// by explicit <c>numberDecimalSeparator</c> / <c>numberGroupSeparator</c> —
/// pass the separators explicitly for deterministic output: containers
/// without ICU fall back to invariant globalization where any culture name
/// formats with "." decimals. An optional <c>suffix</c> (e.g. a currency
/// code) is appended to non-empty values. The base64-encoded PDF is written
/// to <see cref="TargetPathNodeConfiguration.TargetPath"/>.
/// </para>
/// </summary>
[NodeName("RenderDataSheetPdf", 1)]
public record RenderDataSheetPdfNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Optional path to write the rendered PDF's byte length to (as a long), for
    /// feeding a following <c>CreateFileSystemUpdate@1</c>.
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }
}
