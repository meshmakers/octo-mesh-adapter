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
}