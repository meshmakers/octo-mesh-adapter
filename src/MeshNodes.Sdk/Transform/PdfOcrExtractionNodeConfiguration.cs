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
    public int[]? PageNumbers { get; set; }
    
    /// <summary>
    /// OCR language code (e.g., 'en', 'de', 'fr')
    /// </summary>
    public string Language { get; set; } = "en";
    
    /// <summary>
    /// Whether to extract tables from the PDF
    /// </summary>
    public bool ExtractTables { get; set; } = false;
    
    /// <summary>
    /// Output path for extracted tables
    /// </summary>
    public string? TablesOutputPath { get; set; }
    
    /// <summary>
    /// Whether to extract barcodes from the PDF
    /// </summary>
    public bool ExtractBarcodes { get; set; } = false;
    
    /// <summary>
    /// Output path for extracted barcodes
    /// </summary>
    public string? BarcodesOutputPath { get; set; }
    
    /// <summary>
    /// Whether to include OCR confidence score in output
    /// </summary>
    public bool IncludeConfidence { get; set; } = false;
    
    /// <summary>
    /// Output path for OCR confidence score
    /// </summary>
    public string? ConfidenceOutputPath { get; set; }
    
    /// <summary>
    /// Whether to continue processing if OCR extraction fails
    /// </summary>
    public bool ContinueOnError { get; set; } = false;
}