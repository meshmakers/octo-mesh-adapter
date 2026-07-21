using System.Text;
using IronOcr;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.Pdf;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(PdfOcrExtractionNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class PdfOcrExtractionNode(NodeDelegate next, IPdfTextExtractor pdfTextExtractor) : IPipelineNode
{
    private const string TierTextLayer = "TextLayer";
    private const string TierMixed = "Mixed";
    private const string TierOcr = "Ocr";

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
            nodeContext.Debug($"Starting extraction for {(isPdf ? "PDF" : "image")} ({fileData.Length} bytes)");

            // ------------------------------------------------------------------
            // Extraction ladder (only for PDFs, opt-in):
            //   1. Embedded e-invoice XML attachment (ZUGFeRD/Factur-X/XRechnung)
            //   2. Embedded text layer (lossless, per page)
            //   3. OCR — only for pages without a text layer / non-PDF input
            // ------------------------------------------------------------------
            PdfTextExtractionResult? textLayerResult = null;
            if (isPdf && (config.PreferTextLayer || config.ExtractEmbeddedXml))
            {
                try
                {
                    textLayerResult = pdfTextExtractor.Extract(fileData, config.TextLayerMinChars);
                }
                catch (Exception ex)
                {
                    // A malformed PDF must not break the pre-existing OCR path — fall through.
                    nodeContext.Warning($"Text-layer extraction failed, falling back to OCR: {ex.Message}");
                }
            }

            // Tier 1: embedded e-invoice XML
            if (config.ExtractEmbeddedXml && textLayerResult is { EmbeddedFiles.Count: > 0 })
            {
                var xmlFile = textLayerResult.EmbeddedFiles.FirstOrDefault(f =>
                    f.Name.EndsWith(".xml", StringComparison.OrdinalIgnoreCase));
                if (xmlFile != null)
                {
                    dataContext.Set(
                        config.EmbeddedXmlOutputPath ?? "$.EmbeddedXml",
                        Encoding.UTF8.GetString(xmlFile.Data),
                        config.DocumentMode,
                        config.TargetValueKind,
                        config.TargetValueWriteMode
                    );
                    nodeContext.Info($"Extracted embedded XML attachment '{xmlFile.Name}' ({xmlFile.Data.Length} bytes)");
                }
            }

            // Tier 2: text layer — decide which pages still need OCR.
            string? extractedText = null;
            var extractionTier = TierOcr;
            List<PdfPageText>? pagesWithLayer = null;

            if (config.PreferTextLayer && textLayerResult is { Pages.Count: > 0 })
            {
                var pagesMissingLayer = textLayerResult.Pages.Count(p => !p.HasTextLayer);
                if (pagesMissingLayer == 0)
                {
                    extractedText = string.Join("\n\n", textLayerResult.Pages.Select(p => p.Text));
                    extractionTier = TierTextLayer;
                    nodeContext.Info(
                        $"All {textLayerResult.Pages.Count} page(s) have a text layer; OCR skipped");
                }
                else if (pagesMissingLayer < textLayerResult.Pages.Count)
                {
                    pagesWithLayer = textLayerResult.Pages.ToList();
                    extractionTier = TierMixed;
                    nodeContext.Info(
                        $"{textLayerResult.Pages.Count - pagesMissingLayer} of {textLayerResult.Pages.Count} page(s) have a text layer; OCR runs for the rest");
                }
                else
                {
                    nodeContext.Info("No page has a usable text layer; using OCR for the whole document");
                }
            }

            // Tier 3: OCR (pre-existing behavior) — runs unless the text layer covered everything.
            if (extractedText == null)
            {
                var result = RunOcr(config, fileData, isPdf);

                // OcrResult pages are 0-based in document order; PdfPageText.PageNumber is 1-based.
                var ocrPageTexts = result.Pages?.Select(p => p.Text).ToList();
                if (pagesWithLayer != null && ocrPageTexts is { Count: > 0 })
                {
                    // Mixed: text-layer pages stay lossless; OCR fills the gaps (page order kept).
                    var merged = new List<string>(pagesWithLayer.Count);
                    foreach (var page in pagesWithLayer)
                    {
                        if (page.HasTextLayer)
                        {
                            merged.Add(page.Text);
                        }
                        else
                        {
                            var ocrIndex = page.PageNumber - 1;
                            merged.Add(ocrIndex >= 0 && ocrIndex < ocrPageTexts.Count
                                ? ocrPageTexts[ocrIndex]
                                : string.Empty);
                        }
                    }

                    extractedText = string.Join("\n\n", merged);
                }
                else
                {
                    extractedText = result.Text;
                    extractionTier = TierOcr;
                }

                EmitOcrExtras(config, dataContext, nodeContext, result);
            }
            else if (config.ExtractTables || config.ExtractBarcodes || config.IncludeConfidence)
            {
                nodeContext.Warning(
                    "ExtractTables/ExtractBarcodes/IncludeConfidence are OCR features and were skipped because the text layer covered all pages (preferTextLayer)");
            }

            dataContext.Set(
                config.TargetPath,
                extractedText,
                config.DocumentMode,
                config.TargetValueKind,
                config.TargetValueWriteMode
            );

            if (config.PreferTextLayer || config.ExtractionTierOutputPath != null)
            {
                dataContext.Set(
                    config.ExtractionTierOutputPath ?? "$.ExtractionTier",
                    extractionTier,
                    config.DocumentMode,
                    config.TargetValueKind,
                    config.TargetValueWriteMode
                );
            }

            nodeContext.Info(
                $"Successfully extracted {extractedText.Length} characters from {(isPdf ? "PDF" : "image")} (tier: {extractionTier})");
        }
        catch (Exception ex)
        {
            if (!config.ContinueOnError)
            {
                throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext, ex);
            }

            nodeContext.Error($"Error during PDF extraction: {ex.Message}");
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Pre-existing IronOCR path, unchanged in behavior.
    /// </summary>
    private static OcrResult RunOcr(PdfOcrExtractionNodeConfiguration config, byte[] fileData, bool isPdf)
    {
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

        return ocr.Read(ocrInput);
    }

    /// <summary>
    /// Emits the OCR-only side outputs (tables, barcodes, confidence) — pre-existing behavior.
    /// </summary>
    private static void EmitOcrExtras(PdfOcrExtractionNodeConfiguration config, IDataContext dataContext,
        INodeContext nodeContext, OcrResult result)
    {
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
