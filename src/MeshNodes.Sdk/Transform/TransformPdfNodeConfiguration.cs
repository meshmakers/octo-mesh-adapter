using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for <c>TransformPdf@1</c>. Assembles an output PDF from an ordered
/// list of page operations (<see cref="OpsPath"/>) over one or more source PDFs
/// (an array of base64 strings at <see cref="SourceTargetPathNodeConfiguration.Path"/>).
/// Each op selects a page from a source and optionally rotates and/or crops it; the
/// op order defines the output page order, and pages not referenced by any op are
/// dropped. This single contract covers rotate, crop, reorder, delete, split-select
/// and merge (across sources) — the document page editor's server side (AB#4760).
/// </summary>
/// <remarks>
/// The op shape read from <see cref="OpsPath"/> is an array of
/// <c>{ sourceIndex:int, pageIndex:int, rotate?:0|90|180|270, crop?:{ x, y, width, height } }</c>:
/// <list type="bullet">
/// <item><c>sourceIndex</c> — 0-based index into the base64 source array at <c>Path</c>.</item>
/// <item><c>pageIndex</c> — 0-based page within that source.</item>
/// <item><c>rotate</c> — clockwise degrees ADDED on top of the page's existing rotation.</item>
/// <item><c>crop</c> — a rectangle normalized to [0,1], top-left origin, expressed in the
/// page's FINAL displayed orientation (i.e. after the page's existing rotation plus
/// <c>rotate</c>) — the same orientation the editor renders. Omitted / zero-size = no crop.</item>
/// </list>
/// </remarks>
[NodeName("TransformPdf", 1)]
public record TransformPdfNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// JSONPath of the ordered page-operation array. Each element selects a page from a
    /// source PDF (by <c>sourceIndex</c>/<c>pageIndex</c>) and optionally rotates/crops it.
    /// The op order is the output page order.
    /// </summary>
    [PropertyGroup("Paths", 1, "jsonpath")]
    public string OpsPath { get; set; } = "$.ops";

    /// <summary>
    /// When <c>true</c>, a source PDF that cannot be imported (encrypted, corrupt or an
    /// unsupported version) aborts the node as soon as an op references it. When
    /// <c>false</c> (default) ops referencing an unreadable source are skipped with a
    /// warning and the remaining pages are still assembled.
    /// </summary>
    [PropertyGroup("Behavior", 0)]
    public bool FailOnInvalidPdf { get; set; } = false;

    /// <summary>
    /// Optional path to write the produced PDF's byte length to (as a long), for feeding a
    /// following <c>CreateFileSystemUpdate@1</c>.
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }

    /// <summary>
    /// When <c>true</c>, the produced PDF is written to the per-execution scratch space and
    /// <see cref="TargetPathNodeConfiguration.TargetPath"/> receives a small
    /// <c>ScratchFileReference</c> (<c>scratchFileToken</c> + <c>length</c>) instead of a
    /// base64 string — keeping large PDFs off the JSON data context / LOH, exactly like
    /// <c>MergePdf@1</c>. Falls back to base64 when no scratch space is available. Default <c>false</c>.
    /// </summary>
    [PropertyGroup("Behavior", 1)]
    public bool OutputAsScratchFile { get; set; } = false;
}
