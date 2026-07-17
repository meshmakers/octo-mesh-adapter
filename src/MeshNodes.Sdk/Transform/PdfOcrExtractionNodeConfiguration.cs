using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for PDF OCR extraction node that uses IronOCR to extract text and data from PDF files
/// </summary>
[NodeName("PdfOcrExtraction", 1)]
public record PdfOcrExtractionNodeConfiguration : SourceTargetPathNodeConfiguration
{
    /// <summary>
    /// Specific page numbers to process (if not set, all pages will be processed)
    /// </summary>
    [PropertyGroup("Options", 0)]
    public int[]? PageNumbers { get; set; }

    /// <summary>
    /// OCR language code (e.g., 'en', 'de', 'fr')
    /// </summary>
    [PropertyGroup("Options", 1)]
    public string Language { get; set; } = "en";

    /// <summary>
    /// Whether to extract tables from the PDF
    /// </summary>
    [PropertyGroup("Options", 2)]
    public bool ExtractTables { get; set; } = false;

    /// <summary>
    /// Output path for extracted tables
    /// </summary>
    [PropertyGroup("Output", 0, "jsonpath")]
    public string? TablesOutputPath { get; set; }

    /// <summary>
    /// Whether to extract barcodes from the PDF
    /// </summary>
    [PropertyGroup("Options", 3)]
    public bool ExtractBarcodes { get; set; } = false;

    /// <summary>
    /// Output path for extracted barcodes
    /// </summary>
    [PropertyGroup("Output", 1, "jsonpath")]
    public string? BarcodesOutputPath { get; set; }

    /// <summary>
    /// Whether to include OCR confidence score in output
    /// </summary>
    [PropertyGroup("Options", 4)]
    public bool IncludeConfidence { get; set; } = false;

    /// <summary>
    /// Output path for OCR confidence score
    /// </summary>
    [PropertyGroup("Output", 2, "jsonpath")]
    public string? ConfidenceOutputPath { get; set; }

    /// <summary>
    /// Whether to continue processing if OCR extraction fails
    /// </summary>
    [PropertyGroup("Options", 5)]
    public bool ContinueOnError { get; set; } = false;

    /// <summary>
    /// Maximum accepted PDF size in bytes. Files larger than this abort the node
    /// with a FileTooLarge error. Defaults to 1 MB (the previously hard-coded
    /// limit); raise it for pipelines that process real-world scans.
    /// </summary>
    [PropertyGroup("Options", 6)]
    public int MaxFileSizeBytes { get; set; } = 1_000_000;

    /// <summary>
    /// Input handling: the node auto-detects PDF vs. image (JPEG/PNG/TIFF/…) by
    /// magic bytes. When the input is an image and this is enabled (default),
    /// IronOCR pre-processing filters (deskew + denoise) are applied first so
    /// casual phone photos of documents OCR much better. Disable to feed the raw
    /// image unmodified.
    /// </summary>
    [PropertyGroup("Options", 7)]
    public bool EnhanceImage { get; set; } = true;

    /// <summary>
    /// Maximum in-plane skew angle (degrees) the deskew filter corrects on image
    /// input. Higher values catch more tilt but are slower and can misfire.
    /// Only used when <see cref="EnhanceImage"/> is enabled. Note: this corrects
    /// rotation, not perspective distortion (angled shots) — that needs a
    /// separate document-detection step.
    /// </summary>
    [PropertyGroup("Options", 8)]
    public int MaxDeskewAngle { get; set; } = 40;
}