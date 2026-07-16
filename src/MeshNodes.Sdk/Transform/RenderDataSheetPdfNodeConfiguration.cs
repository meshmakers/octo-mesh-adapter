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
///   "sections": [
///     { "heading": "Document", "rows": [ { "label": "Number", "value": "RE-2025-001" } ] }
///   ],
///   "footerHeading": "Note to tax advisor",
///   "footerText": "Please book against travel expenses."
/// }
/// </code>
/// Keys are matched case-insensitively. The base64-encoded PDF is written to
/// <see cref="TargetPathNodeConfiguration.TargetPath"/>.
/// </para>
/// </summary>
[NodeName("RenderDataSheetPdf", 1)]
public record RenderDataSheetPdfNodeConfiguration : SourceTargetPathNodeConfiguration;
