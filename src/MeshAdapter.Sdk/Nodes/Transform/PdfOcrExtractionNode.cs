using System.Text;
using System.Text.RegularExpressions;
using IronOcr;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.Services;
using UglyToad.PdfPig;
using UglyToad.PdfPig.DocumentLayoutAnalysis.TextExtractor;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

[NodeConfiguration(typeof(PdfOcrExtractionNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal partial class PdfOcrExtractionNode(NodeDelegate next) : IPipelineNode
{
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

            nodeContext.Debug($"Starting OCR extraction for {(isPdf ? "PDF" : "image")} ({fileData.Length} bytes)");

            // Digital PDFs carry an exact embedded text layer; prefer it over raster+OCR.
            // Tesseract drops separator-less alphanumeric codes (e.g. invoice numbers) and
            // mangles non-German diacritics, whereas the text layer is verbatim. Tables and
            // barcodes only come from the OCR path, so the shortcut is skipped when either is
            // requested; scanned/image PDFs (empty text layer) fall through to OCR. AB#4528.
            // Hybrid PDFs break the either/or above: the text layer carries the LABELS while
            // the FIGURES are embedded images, so the shortcut hands the AI stage an invoice
            // without a single amount (prod-1 2026-09-15, "26034 Lehrlingstraining": text
            // layer had "Gesamt"/"MWSt 20%"/"Bruttosumme", the amount column was one of 43
            // stencil images). When set, this carries the text layer into the OCR branch
            // below so both reads can be merged instead of one replacing the other. AB#5259.
            string? textLayerToMerge = null;

            var handledByTextLayer = false;
            if (isPdf && config.PreferTextLayer && !config.ExtractTables && !config.ExtractBarcodes)
            {
                var textLayer = TryExtractPdfTextLayer(fileData, config.PageNumbers, nodeContext);
                if (textLayer is not null
                    && textLayer.Length >= config.MinTextLayerChars
                    && config.MergeOcrOnIncompleteTextLayer
                    && !IsTextLayerStructurallyComplete(textLayer, config.AmountLabels, out var unresolvedLabel))
                {
                    // Not a digital PDF after all — it only looks like one. Keep the text
                    // layer (it stays authoritative) and fall through to OCR for the gaps.
                    textLayerToMerge = textLayer;
                    nodeContext.Info(
                        $"PDF text layer carries the label '{unresolvedLabel}' without an adjacent amount — running OCR in addition and merging both reads");
                }
                else if (textLayer is not null && textLayer.Length >= config.MinTextLayerChars)
                {
                    dataContext.Set(
                        config.TargetPath,
                        textLayer,
                        config.DocumentMode,
                        config.TargetValueKind,
                        config.TargetValueWriteMode
                    );

                    if (config.IncludeConfidence)
                    {
                        // The text layer is authoritative, not a probabilistic OCR read.
                        dataContext.Set(
                            config.ConfidenceOutputPath ?? "$.Confidence",
                            100d,
                            config.DocumentMode,
                            config.TargetValueKind,
                            config.TargetValueWriteMode
                        );
                    }

                    handledByTextLayer = true;
                    nodeContext.Info($"Extracted {textLayer.Length} characters from the PDF text layer (OCR skipped)");
                }
                else
                {
                    nodeContext.Debug($"PDF text layer absent or below {config.MinTextLayerChars} chars — falling back to OCR");
                }
            }

            if (!handledByTextLayer)
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

            var result = ocr.Read(ocrInput);

            var extractedText = result.Text;

            if (textLayerToMerge is not null)
            {
                // Merge, do not replace: the text layer is verbatim where it has content
                // (AB#4528 — Tesseract drops separator-less invoice numbers and mangles
                // diacritics), OCR only supplies what the text layer is missing. The
                // confidence written further down is the OCR score, not the 100 of the
                // pure text-layer path — part of this result IS a probabilistic read. AB#5259.
                var merged = MergeTextLayerWithOcr(textLayerToMerge, extractedText);
                nodeContext.Info(
                    $"Merged PDF text layer ({textLayerToMerge.Length} chars) with OCR ({extractedText.Length} chars) into {merged.Length} chars");
                extractedText = merged;
            }

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

            } // end !handledByTextLayer
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
    /// Extracts the embedded text layer of a digital PDF in reading order (PdfPig).
    /// Returns <c>null</c> on any parse failure — encrypted, malformed, or an image-only
    /// PDF with no text — so the caller transparently falls back to OCR. AB#4528.
    /// </summary>
    private static string? TryExtractPdfTextLayer(byte[] fileData, int[]? pageNumbers, INodeContext nodeContext)
    {
        try
        {
            using var document = PdfDocument.Open(fileData);

            var pages = pageNumbers is { Length: > 0 }
                ? pageNumbers.Where(p => p >= 1 && p <= document.NumberOfPages)
                : Enumerable.Range(1, document.NumberOfPages);

            var sb = new StringBuilder();
            foreach (var pageNumber in pages)
            {
                sb.AppendLine(ContentOrderTextExtractor.GetText(document.GetPage(pageNumber)));
            }

            return sb.ToString();
        }
        catch (Exception ex)
        {
            nodeContext.Debug($"PDF text-layer extraction failed ({ex.Message}); falling back to OCR");
            return null;
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