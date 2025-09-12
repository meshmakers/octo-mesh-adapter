# PDF OCR Extraction Node Documentation

## Overview

The PDF OCR Extraction Node is a transform node that uses IronOCR to extract text and data from PDF files. It provides comprehensive OCR capabilities including text extraction, table detection, barcode recognition, and confidence scoring.

## Node Configuration

### Node Name
`PdfOcrExtraction` (Version 1)

### Configuration Class
`PdfOcrExtractionNodeConfiguration`

## Input Sources

The node supports two methods for providing PDF data:

### 1. Context Path Input
Use the `InputPath` property to specify a JSON path where PDF data (as byte array) is stored in the pipeline context.

```json
{
  "InputPath": "$.PdfData"
}
```

### 2. File Path Input
Use the `FilePath` property to specify a direct file system path to the PDF file.

```json
{
  "FilePath": "/path/to/document.pdf"
}
```

**Note:** If both `InputPath` and `FilePath` are specified, `InputPath` takes precedence.

## Configuration Properties

### Basic Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `InputPath` | `string?` | `null` | JSON path to PDF data in context (byte array) |
| `FilePath` | `string?` | `null` | Direct file system path to PDF file |
| `TargetPath` | `string` | Required | Output path for extracted text |
| `Language` | `string` | `"en"` | OCR language code |
| `ContinueOnError` | `bool` | `false` | Whether to continue pipeline execution on OCR failure |

### Page Selection

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `PageNumbers` | `int[]?` | `null` | Specific page numbers to process (1-based indexing). If null, all pages are processed |

### Advanced Features

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `ExtractTables` | `bool` | `false` | Enable table extraction from PDF |
| `TablesOutputPath` | `string?` | `"$.Tables"` | Output path for extracted tables |
| `ExtractBarcodes` | `bool` | `false` | Enable barcode detection and extraction |
| `BarcodesOutputPath` | `string?` | `"$.Barcodes"` | Output path for detected barcodes |
| `IncludeConfidence` | `bool` | `false` | Include OCR confidence score in output |
| `ConfidenceOutputPath` | `string?` | `"$.Confidence"` | Output path for confidence score |

### Pipeline Configuration

| Property | Type | Default | Description |
|----------|------|---------|-------------|
| `DocumentMode` | `DocumentMode` | `Document` | Document processing mode |
| `TargetValueKind` | `ValueKind` | `Simple` | Target value type |
| `TargetValueWriteMode` | `ValueWriteMode` | `Set` | How to write the target value |

## Supported Languages

The node supports multiple OCR languages through the `Language` property:

| Language Code | Language | IronOCR Mapping |
|---------------|----------|-----------------|
| `"en"`, `"english"` | English | `OcrLanguage.English` |
| `"de"`, `"german"` | German | `OcrLanguage.German` |
| `"fr"`, `"french"` | French | `OcrLanguage.French` |
| `"es"`, `"spanish"` | Spanish | `OcrLanguage.Spanish` |
| `"it"`, `"italian"` | Italian | `OcrLanguage.Italian` |
| `"pt"`, `"portuguese"` | Portuguese | `OcrLanguage.Portuguese` |
| `"nl"`, `"dutch"` | Dutch | `OcrLanguage.Dutch` |
| `"ru"`, `"russian"` | Russian | `OcrLanguage.Russian` |
| `"zh"`, `"chinese"` | Chinese (Simplified) | `OcrLanguage.ChineseSimplified` |
| `"ja"`, `"japanese"` | Japanese | `OcrLanguage.Japanese` |
| `"ko"`, `"korean"` | Korean | `OcrLanguage.Korean` |
| `"ar"`, `"arabic"` | Arabic | `OcrLanguage.Arabic` |

## Usage Examples

### Basic Text Extraction

```json
{
  "NodeName": "PdfOcrExtraction",
  "Configuration": {
    "FilePath": "/documents/invoice.pdf",
    "TargetPath": "$.ExtractedText",
    "Language": "en"
  }
}
```

### Advanced Extraction with Tables and Barcodes

```json
{
  "NodeName": "PdfOcrExtraction",
  "Configuration": {
    "InputPath": "$.PdfDocument",
    "TargetPath": "$.DocumentText",
    "Language": "de",
    "ExtractTables": true,
    "TablesOutputPath": "$.DocumentTables",
    "ExtractBarcodes": true,
    "BarcodesOutputPath": "$.DocumentBarcodes",
    "IncludeConfidence": true,
    "ConfidenceOutputPath": "$.OcrConfidence",
    "PageNumbers": [1, 2, 3]
  }
}
```

### Processing with Error Handling

```json
{
  "NodeName": "PdfOcrExtraction",
  "Configuration": {
    "FilePath": "/path/to/document.pdf",
    "TargetPath": "$.ProcessedText",
    "ContinueOnError": true,
    "Language": "fr"
  }
}
```

## Output Data Structure

### Text Output
The extracted text is output as a string to the specified `TargetPath`.

### Table Output
When `ExtractTables` is enabled, table data is output as an array of `IronOcr.OcrResult.Table` objects.

### Barcode Output
When `ExtractBarcodes` is enabled, barcode data is output as an array of `IronOcr.OcrResult.Barcode` objects.

### Confidence Output
When `IncludeConfidence` is enabled, the OCR confidence score (0.0 to 1.0) is output as a double value.

## Error Handling

The node provides comprehensive error handling:

1. **File Not Found**: If `FilePath` is specified but the file doesn't exist, an error is logged and the pipeline continues.
2. **No PDF Data**: If neither input method provides valid PDF data, a warning is logged and the pipeline continues.
3. **OCR Failures**: OCR processing errors are logged. If `ContinueOnError` is `false` (default), the exception is rethrown, stopping the pipeline. If `true`, the pipeline continues.

## Logging

The node provides detailed logging at different levels:

- **Debug**: OCR process start, table/barcode detection counts
- **Info**: Successful extraction with character count
- **Warning**: No PDF data available
- **Error**: File not found, OCR processing errors

## Performance Considerations

1. **Memory Usage**: Large PDF files will consume significant memory during processing.
2. **Processing Time**: OCR is computationally intensive. Processing time increases with:
   - Number of pages
   - Image resolution
   - Complexity of content
3. **Language Models**: Each language requires specific OCR models, which may impact first-run performance.

## Dependencies

- **IronOCR**: Version 2025.9.7 or later
- **Meshmakers.Octo.Sdk.Common.EtlDataPipeline**: For pipeline integration
- **.NET 9.0**: Runtime requirement

## Limitations

1. **Page Selection**: The current implementation processes all pages when `PageNumbers` is specified due to IronOCR API limitations.
2. **File Format**: Only PDF files are supported.
3. **OCR Accuracy**: Accuracy depends on image quality, font types, and document layout.

## Best Practices

1. **Language Selection**: Always specify the correct language for optimal OCR accuracy.
2. **Error Handling**: Use `ContinueOnError: true` for batch processing scenarios.
3. **Resource Management**: Monitor memory usage when processing large documents.
4. **Path Validation**: Ensure file paths are accessible and PDF data is valid before processing.
5. **Output Paths**: Use descriptive output paths to organize extracted data effectively.