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
    private const string TierTextLayerFromOcr = "TextLayerFromOcr";
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

            // Invoice-archive systems (e.g. UTA's "Transform Foundation Server") deliver PDFs
            // with a proprietary metadata preamble before the %PDF header; the spec grants
            // readers a 1024-byte tolerance for locating it. Strip the preamble so IronOCR's
            // strict input classification sees the document from the real header on. AB#4533.
            var pdfHeaderOffset = FindPdfHeader(fileData);
            var isPdf = pdfHeaderOffset >= 0;
            if (pdfHeaderOffset > 0)
            {
                nodeContext.Debug($"PDF header found at offset {pdfHeaderOffset}; stripping preamble");
                fileData = fileData[pdfHeaderOffset..];
            }

            nodeContext.Debug($"Starting extraction for {(isPdf ? "PDF" : "image")} ({fileData.Length} bytes)");

            // Extraction ladder (PDF only): pages with a usable embedded text layer are
            // read losslessly, only the rest is OCR'd. Tables, barcodes and explicit page
            // selection are OCR-path features, so the text-layer tier stands aside when
            // they are requested.
            var textLayerEligible = config.PreferTextLayer && !config.ExtractTables && !config.ExtractBarcodes
                                    && config.PageNumbers is not { Length: > 0 };
            PdfTextExtractionResult? textLayerResult = null;
            if (isPdf && textLayerEligible)
            {
                try
                {
                    textLayerResult = pdfTextExtractor.Extract(fileData, config.MinTextLayerChars);
                }
                catch (Exception ex)
                {
                    // A malformed PDF must not break the OCR path — fall through.
                    nodeContext.Warning($"Text-layer extraction failed, falling back to OCR: {ex.Message}");
                }
            }

            // Tier 1: text layer — determine which pages still need OCR.
            string? extractedText = null;
            var extractionTier = TierOcr;
            List<PdfPageText>? pagesWithLayer = null;

            if (textLayerEligible && textLayerResult is { Pages.Count: > 0 })
            {
                var pagesMissingLayer = textLayerResult.Pages.Count(p => !p.HasTextLayer);
                if (pagesMissingLayer == 0)
                {
                    extractedText = string.Join("\n\n", textLayerResult.Pages.Select(p => p.Text));
                    // Text-on-image pages (scans with a baked-in OCR layer) downgrade the
                    // tier: usable text, but OCR-grade trust rather than born-digital fidelity.
                    var textOnImagePages = textLayerResult.Pages.Count(p => p.IsTextOnImage);
                    extractionTier = textOnImagePages > 0 ? TierTextLayerFromOcr : TierTextLayer;
                    nodeContext.Info(textOnImagePages > 0
                        ? $"All {textLayerResult.Pages.Count} page(s) have a text layer, but {textOnImagePages} are text-on-image (previous OCR pass); OCR skipped, trust downgraded"
                        : $"All {textLayerResult.Pages.Count} page(s) have a text layer; OCR skipped");
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

            // Tier 2: OCR — runs unless the text layer covered every page.
            if (extractedText == null)
            {
                // OCR only what still needs it: the pages without a text layer in the mixed case,
                // or the configured page selection. Rasterizing and recognizing pages whose text
                // is then discarded was the dominant cost on mixed documents.
                var ocrPageIndices = pagesWithLayer != null
                    ? PagesWithoutLayer(pagesWithLayer)
                    : SelectedPageIndices(config);
                var result = RunOcr(config, fileData, isPdf, ocrPageIndices);

                // OcrResult pages come back in the order requested; PdfPageText.PageNumber is 1-based.
                var ocrPageTexts = result.Pages?.Select(p => p.Text).ToList();
                if (pagesWithLayer != null && ocrPageTexts is { Count: > 0 })
                {
                    if (ocrPageTexts.Count != ocrPageIndices!.Count)
                    {
                        nodeContext.Warning(
                            $"OCR returned {ocrPageTexts.Count} page(s) for {ocrPageIndices.Count} requested; " +
                            "pages without a result are left empty");
                    }

                    // Mixed: text-layer pages stay lossless; OCR fills the gaps (page order kept).
                    extractedText = MergeMixedPages(pagesWithLayer, ocrPageTexts);
                }
                else
                {
                    extractedText = result.Text;
                    extractionTier = TierOcr;
                }

                EmitOcrExtras(config, dataContext, nodeContext, result);
            }
            else if (config.IncludeConfidence && extractionTier == TierTextLayer)
            {
                // A born-digital text layer is authoritative, not a probabilistic OCR read.
                dataContext.Set(
                    config.ConfidenceOutputPath ?? "$.Confidence",
                    100d,
                    config.DocumentMode,
                    config.TargetValueKind,
                    config.TargetValueWriteMode
                );
            }
            else if (config.IncludeConfidence)
            {
                // Text-on-image layers stem from an earlier OCR pass whose confidence is
                // unknown — no value beats a fabricated 100. ExtractionTier carries the
                // trust signal instead.
                nodeContext.Debug(
                    "Confidence not emitted: the text layer stems from a previous OCR pass (see ExtractionTier)");
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
    /// <summary>
    /// Zero-based indices of the pages that have no usable text layer, in document order — the
    /// pages the OCR pass has to cover in the mixed case.
    /// </summary>
    internal static IReadOnlyList<int> PagesWithoutLayer(IReadOnlyList<PdfPageText> pages) =>
        pages.Where(p => !p.HasTextLayer).Select(p => p.PageNumber - 1).Where(i => i >= 0).ToList();

    /// <summary>
    /// Zero-based indices for a configured <see cref="PdfOcrExtractionNodeConfiguration.PageNumbers"/>
    /// selection (1-based on the configuration); null when every page is to be processed.
    /// </summary>
    internal static IReadOnlyList<int>? SelectedPageIndices(PdfOcrExtractionNodeConfiguration config) =>
        config.PageNumbers is { Length: > 0 }
            ? config.PageNumbers.Where(n => n >= 1).Select(n => n - 1).Distinct().OrderBy(i => i).ToList()
            : null;

    /// <summary>
    /// Interleaves text-layer pages with the OCR results of the pages that lacked one. The OCR
    /// texts arrive in the order of <see cref="PagesWithoutLayer"/>, so they are consumed
    /// sequentially rather than indexed by absolute page number — the OCR pass no longer covers
    /// every page. A missing OCR result leaves that page empty instead of shifting the others.
    /// </summary>
    internal static string MergeMixedPages(IReadOnlyList<PdfPageText> pages,
        IReadOnlyList<string> ocrTextsForPagesWithoutLayer)
    {
        var merged = new List<string>(pages.Count);
        var nextOcr = 0;
        foreach (var page in pages)
        {
            if (page.HasTextLayer)
            {
                merged.Add(page.Text);
            }
            else
            {
                merged.Add(nextOcr < ocrTextsForPagesWithoutLayer.Count
                    ? ocrTextsForPagesWithoutLayer[nextOcr++]
                    : string.Empty);
            }
        }

        return string.Join("\n\n", merged);
    }

    private static OcrResult RunOcr(PdfOcrExtractionNodeConfiguration config, byte[] fileData, bool isPdf,
        IReadOnlyList<int>? pageIndices)
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

        // IronOCR page indices are zero-based; null means the whole document.
        using OcrInputBase ocrInput = isPdf
            ? pageIndices is { Count: > 0 }
                ? new OcrPdfInput(fileData, PageIndices: pageIndices)
                : new OcrPdfInput(fileData)
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
    /// Locates the <c>%PDF-</c> magic header within the first 1024 bytes — the tolerance
    /// the PDF spec grants readers, needed for archive systems that prepend metadata
    /// before the header (AB#4533). Returns the header offset, or -1 for anything that
    /// is not a PDF (JPEG/PNG/TIFF/… are handled as images via <see cref="OcrImageInput"/>).
    /// </summary>
    private static int FindPdfHeader(byte[] data)
    {
        ReadOnlySpan<byte> marker = "%PDF-"u8;
        var searchLength = Math.Min(data.Length, 1024 + marker.Length);
        return data.AsSpan(0, searchLength).IndexOf(marker);
    }

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
