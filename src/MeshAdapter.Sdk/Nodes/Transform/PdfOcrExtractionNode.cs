using IronOcr;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(PdfOcrExtractionNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class PdfOcrExtractionNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<PdfOcrExtractionNodeConfiguration>();
        
        try
        {
            if (string.IsNullOrEmpty(config.Path))
            {
                throw MeshAdapterPipelineExecutionException.PathParameterValueMissing(nodeContext, nameof(config.Path));
            }

            var content = dataContext.Get<string>(config.Path);
            if (string.IsNullOrEmpty(content))
            {
                throw PipelineExecutionException.ValueNotSet(nodeContext, config.Path);
            }

            var fileData = Convert.FromBase64String(content);

            if (fileData.Length > config.MaxFileSizeBytes)
            {
                throw MeshAdapterPipelineExecutionException.FileTooLarge(nodeContext, fileData.Length, config.MaxFileSizeBytes);
            }

            var isPdf = IsPdf(fileData);
            nodeContext.Debug($"Starting OCR extraction for {(isPdf ? "PDF" : "image")} ({fileData.Length} bytes)");

            // Initialize IronOCR with explicit configuration
            License.LicenseKey = "IRONOCR.MESHMAKERSGMBH.IRO250912.8133.59109-FC1A47E4E8-DIQDFCQLZZTUL5T-F2N36ZLSCQMG-23LQGHXXX55Q-IZPR6FYUCMKB-IQFDUBDINX2G-H6YOXX-L6GROAER3DWRUA-IRONOCR.DOTNET.LITE.SUB-3A6DS3.RENEW.SUPPORT.12.SEP.2026"; // Add license key if you have one
            var ocr = new IronTesseract();
            
            if (!string.IsNullOrEmpty(config.Language))
            {
                ocr.Language = GetOcrLanguage(config.Language);
            }
            
            if (config.PageNumbers is { Length: > 0 })
            {
                ocr.Configuration.PageSegmentationMode = TesseractPageSegmentationMode.AutoOsd;
            }

            using OcrInputBase ocrInput = isPdf
                ? new OcrPdfInput(fileData)
                : new OcrImageInput(fileData);

            // A photographed document benefits from geometric + noise correction
            // before OCR (deskew straightens tilt, denoise removes sensor grain).
            // Skipped for PDFs (already page-rendered) and when EnhanceImage is off.
            if (!isPdf && config.EnhanceImage)
            {
                ocrInput.Deskew(config.MaxDeskewAngle);
                ocrInput.DeNoise(false);
            }

            var result = ocr.Read(ocrInput);

            var extractedText = result.Text;

            if (config.ExtractTables)
            {
                var tables = result.Tables;
                if (tables is { Length: > 0 })
                {
                    nodeContext.Debug($"Found {tables.Length} tables in PDF");
                    dataContext.Set(
                        config.TablesOutputPath ?? "$.Tables",
                        tables,
                        config.DocumentMode,
                        config.TargetValueKind,
                        config.TargetValueWriteMode
                    );
                }
            }

            if (config.ExtractBarcodes)
            {
                var barcodes = result.Barcodes;
                if (barcodes != null && barcodes.Length > 0)
                {
                    nodeContext.Debug($"Found {barcodes.Length} barcodes in PDF");
                    dataContext.Set(
                        config.BarcodesOutputPath ?? "$.Barcodes",
                        barcodes,
                        config.DocumentMode,
                        config.TargetValueKind,
                        config.TargetValueWriteMode
                    );
                }
            }

            dataContext.Set(
                config.TargetPath,
                extractedText,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode
            );

            if (config.IncludeConfidence)
            {
                dataContext.Set(
                    config.ConfidenceOutputPath ?? "$.Confidence",
                    result.Confidence,
                    config.DocumentMode,
                    config.TargetValueKind,
                    config.TargetValueWriteMode
                );
            }

            nodeContext.Info($"Successfully extracted {extractedText.Length} characters from {(isPdf ? "PDF" : "image")}");
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during PDF OCR extraction: {ex.Message}");
        }
        
        await next(dataContext, nodeContext);
    }
    
    /// <summary>
    /// Detects a PDF by its <c>%PDF-</c> magic header. Anything else (JPEG/PNG/TIFF/…)
    /// is handled as an image via <see cref="OcrImageInput"/>.
    /// </summary>
    private static bool IsPdf(byte[] data) =>
        data.Length >= 5 && data[0] == 0x25 && data[1] == 0x50
                         && data[2] == 0x44 && data[3] == 0x46 && data[4] == 0x2D;

    private static OcrLanguage GetOcrLanguage(string language)
    {
        return language.ToLowerInvariant() switch
        {
            "en" or "english" => OcrLanguage.English,
            "de" or "german" => OcrLanguage.German,
            "fr" or "french" => OcrLanguage.French,
            "es" or "spanish" => OcrLanguage.Spanish,
            "it" or "italian" => OcrLanguage.Italian,
            "pt" or "portuguese" => OcrLanguage.Portuguese,
            "nl" or "dutch" => OcrLanguage.Dutch,
            "ru" or "russian" => OcrLanguage.Russian,
            "zh" or "chinese" => OcrLanguage.ChineseSimplified,
            "ja" or "japanese" => OcrLanguage.Japanese,
            "ko" or "korean" => OcrLanguage.Korean,
            "ar" or "arabic" => OcrLanguage.Arabic,
            _ => OcrLanguage.English
        };
    }
}