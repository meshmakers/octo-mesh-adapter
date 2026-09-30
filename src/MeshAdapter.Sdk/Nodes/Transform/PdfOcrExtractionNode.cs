using System.Text;
using System.Text.RegularExpressions;
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
internal partial class PdfOcrExtractionNode(NodeDelegate next, IPdfTextExtractor pdfTextExtractor) : IPipelineNode
{
    private const string TierTextLayer = "TextLayer";
    private const string TierTextLayerFromOcr = "TextLayerFromOcr";
    private const string TierMixed = "Mixed";
    private const string TierOcr = "Ocr";

    /// <summary>
    /// Header the merged OCR supplement is introduced with. It is prose on purpose: the
    /// consumer of this node's output is an AI extraction prompt, and the marker tells it
    /// that the lines below come from the page image, not from the (authoritative) text
    /// layer above. AB#5259.
    /// </summary>
    internal const string OcrSupplementMarker =
        "--- OCR supplement (read from the page image; the PDF text layer above omitted these) ---";

    /// <summary>
    /// Built-in German/English invoice vocabulary for the text-layer completeness check.
    /// Deliberately restricted to TOTAL-ish labels: the check must fire on an invoice whose
    /// amount column is an image, not on every document that happens to contain a number.
    /// Overridable per pipeline via <see cref="PdfOcrExtractionNodeConfiguration.AmountLabels"/>. AB#5259.
    /// </summary>
    private static readonly string[] DefaultAmountLabels =
    [
        "gesamt", "summe", "brutto", "netto", "mwst", "mehrwertsteuer", "ust.", "umsatzsteuer",
        "rechnungsbetrag", "endbetrag", "zahlbetrag", "zu zahlen", "zwischensumme",
        "total", "subtotal", "amount due", "vat", "net amount", "gross amount"
    ];

    /// <summary>
    /// How far from a label line an amount may sit and still count as "adjacent". Table
    /// layouts flatten to a label line followed by its value line (sometimes with a unit
    /// line in between), hence one line back and two lines forward. AB#5259.
    /// </summary>
    private const int AmountLinesBefore = 1;

    private const int AmountLinesAfter = 2;

    // A monetary amount: German "1.200,00" / "520,00" and English "13.88". The trailing
    // (?![\d.,]) rejects a date component ("30.05.2026" would otherwise match as "30.05"),
    // the leading (?<![\d.,]) stops a match from starting inside a longer number. A bare
    // percentage ("MWSt 20%") or a year deliberately does NOT match — those are exactly
    // the tokens a label-only text layer is left with. AB#5259.
    [GeneratedRegex(@"(?<![\d.,])\d{1,3}(?:[.\u00A0 ]\d{3})*,\d{2}(?![\d.,])|(?<![\d.,])\d+\.\d{2}(?![\d.,])")]
    private static partial Regex MonetaryAmountRegex();

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<PdfOcrExtractionNodeConfiguration>();

        // Configuration error, outside the try: a page selection that leaves no valid page must not
        // silently become "all pages", and ContinueOnError must not turn it into a pass-through.
        if (config.PageNumbers is { Length: > 0 } && SelectedPageIndices(config) is { Count: 0 })
        {
            throw MeshAdapterPipelineExecutionException.ProcessingError(nodeContext,
                new ArgumentException(
                    $"pageNumbers [{string.Join(", ", config.PageNumbers)}] selects no valid page; " +
                    "page numbers are 1-based. Omit pageNumbers to process the whole document.",
                    nameof(config.PageNumbers)));
        }

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
            // A text layer that names amount labels without figures next to them is a hybrid PDF
            // (labels in the layer, figures as images): the layer stays the authoritative base and
            // OCR of the whole document supplements it instead of one read replacing the other.
            string? textLayerToMerge = null;
            var layerNeedsSupplement = false;

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

                    if (config.MergeOcrOnIncompleteTextLayer
                        && !IsTextLayerStructurallyComplete(extractedText, config.AmountLabels, out var unresolvedLabel))
                    {
                        nodeContext.Info(
                            $"Text layer names '{unresolvedLabel}' without an adjacent amount (hybrid PDF); OCR runs in addition and both reads are merged");
                        textLayerToMerge = extractedText;
                        extractedText = null;
                        extractionTier = TierMixed;
                    }
                }
                else if (pagesMissingLayer < textLayerResult.Pages.Count)
                {
                    pagesWithLayer = textLayerResult.Pages.ToList();
                    extractionTier = TierMixed;
                    nodeContext.Info(
                        $"{textLayerResult.Pages.Count - pagesMissingLayer} of {textLayerResult.Pages.Count} page(s) have a text layer; OCR runs for the rest");

                    var layerText = string.Join("\n", pagesWithLayer.Where(p => p.HasTextLayer).Select(p => p.Text));
                    if (config.MergeOcrOnIncompleteTextLayer
                        && !IsTextLayerStructurallyComplete(layerText, config.AmountLabels, out var unresolvedLabel))
                    {
                        nodeContext.Info(
                            $"Text-layer pages name '{unresolvedLabel}' without an adjacent amount (hybrid PDF); OCR runs for every page and supplements the layer");
                        layerNeedsSupplement = true;
                    }
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
                var ocrPageIndices = pagesWithLayer != null && !layerNeedsSupplement
                    ? PagesWithoutLayer(pagesWithLayer)
                    : SelectedPageIndices(config);
                var result = RunOcr(config, fileData, isPdf, ocrPageIndices);

                // OcrResult pages come back in the order requested; PdfPageText.PageNumber is 1-based.
                // Merge whenever pages have a text layer, even when OCR returned no page objects:
                // falling back to result.Text would drop every text-layer page and report Ocr.
                var ocrPageTexts = result.Pages?.Select(p => p.Text).ToList() ?? [];
                if (textLayerToMerge != null)
                {
                    // Every page had a layer, but it lacks the figures: keep it verbatim and
                    // append only the OCR lines that carry tokens the layer does not have.
                    extractedText = MergeTextLayerWithOcr(textLayerToMerge, result.Text);
                    nodeContext.Info(
                        $"Merged PDF text layer ({textLayerToMerge.Length} chars) with OCR ({result.Text.Length} chars) into {extractedText.Length} chars");
                }
                else if (pagesWithLayer != null && layerNeedsSupplement)
                {
                    // OCR covered every page: layer pages keep their layer, the others take their
                    // OCR page, and the OCR read supplements what the layer pages lack.
                    var basePages = pagesWithLayer.Select((p, i) =>
                        p.HasTextLayer ? p.Text : i < ocrPageTexts.Count ? ocrPageTexts[i] : string.Empty);
                    extractedText = MergeTextLayerWithOcr(string.Join("\n\n", basePages), result.Text);
                }
                else if (pagesWithLayer != null)
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
    /// Decides whether an embedded text layer may stand in for OCR on its own. A layer is
    /// structurally INCOMPLETE when it names a total/amount label but has no monetary figure
    /// next to it — the signature of a hybrid PDF whose amount column is an embedded image.
    /// A layer that names no such label at all is not judged (it is not an invoice as far as
    /// this check goes) and counts as complete, so letters and info sheets keep the cheap
    /// text-layer path. AB#5259.
    /// </summary>
    /// <param name="textLayer">The extracted text layer.</param>
    /// <param name="amountLabels">Per-pipeline label override, or <c>null</c> for the built-in set.</param>
    /// <param name="unresolvedLabel">The first label found without an adjacent amount, for logging.</param>
    internal static bool IsTextLayerStructurallyComplete(string textLayer, string[]? amountLabels,
        out string? unresolvedLabel)
    {
        unresolvedLabel = null;

        var labels = amountLabels is { Length: > 0 } ? amountLabels : DefaultAmountLabels;
        var lines = textLayer.Split('\n');

        for (var i = 0; i < lines.Length; i++)
        {
            var line = lines[i];
            var label = labels.FirstOrDefault(l => line.Contains(l, StringComparison.OrdinalIgnoreCase));
            if (label is null)
            {
                continue;
            }

            var from = Math.Max(0, i - AmountLinesBefore);
            var to = Math.Min(lines.Length - 1, i + AmountLinesAfter);

            var hasAmount = false;
            for (var j = from; j <= to && !hasAmount; j++)
            {
                hasAmount = MonetaryAmountRegex().IsMatch(lines[j]);
            }

            if (!hasAmount)
            {
                unresolvedLabel = label;
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Merges an authoritative text layer with a supplementary OCR read. The text layer is
    /// returned verbatim and unmodified; appended below the <see cref="OcrSupplementMarker"/>
    /// are only those OCR lines that contribute at least one token the text layer does not
    /// already have — so labels the text layer already spells correctly are not duplicated in
    /// their mangled OCR form, and the image-only figures are. Returns the text layer
    /// unchanged when OCR found nothing new. AB#5259.
    /// </summary>
    internal static string MergeTextLayerWithOcr(string textLayer, string ocrText)
    {
        if (string.IsNullOrWhiteSpace(ocrText))
        {
            return textLayer;
        }

        var known = new HashSet<string>(Tokenize(textLayer), StringComparer.OrdinalIgnoreCase);

        var additions = ocrText
            .Split('\n')
            .Select(l => l.Trim())
            .Where(l => l.Length > 0 && Tokenize(l).Any(t => !known.Contains(t)))
            .ToList();

        if (additions.Count == 0)
        {
            return textLayer;
        }

        return new StringBuilder(textLayer)
            .AppendLine()
            .AppendLine(OcrSupplementMarker)
            .AppendJoin('\n', additions)
            .ToString();
    }

    /// <summary>
    /// Splits text into whitespace-separated tokens with surrounding punctuation trimmed.
    /// Whitespace (not punctuation) is the separator on purpose: "520,00" must stay ONE
    /// token, otherwise a text layer that contains an unrelated "520" would swallow the
    /// OCR line carrying the amount. AB#5259.
    /// </summary>
    private static IEnumerable<string> Tokenize(string text)
    {
        return text
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .Select(t => t.Trim('.', ',', ':', ';', '(', ')', '[', ']', '"', '\'', '-', '|'))
            .Where(t => t.Length > 0);
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
