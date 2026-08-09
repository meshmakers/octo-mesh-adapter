using System.Globalization;
using System.Text;
using System.Xml.Linq;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that imports a camt.053.001.02 XML bank statement into an array of normalized
/// booking objects (one per Ntry). Parsing is namespace-agnostic (resolves by local element name),
/// so both the ISO standard namespace and the Austrian STUZZA/APC variant are supported by the same
/// node — a namespace-bound parser would silently return zero entries on the other dialect.
/// The emitted objects feed the standard GetOrCreate/CreateUpdateInfo/ApplyChanges flow that creates
/// Basic.Accounting/BankTransaction entities (MatchState=Unreviewed) for the reconciliation pipeline.
/// </summary>
[NodeConfiguration(typeof(ImportFromCamt053NodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class ImportFromCamt053Node(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<ImportFromCamt053NodeConfiguration>();

        var base64 = dataContext.Get<string>($"$.files[{config.FileIndex}].data");
        if (base64 == null)
        {
            nodeContext.Error($"No file found at $.files[{config.FileIndex}].data");
            return;
        }

        byte[] bytes;
        try
        {
            bytes = Convert.FromBase64String(base64);
        }
        catch (FormatException ex)
        {
            nodeContext.Error($"Invalid base64 data: {ex.Message}");
            return;
        }

        var content = GetEncoding(config.Encoding).GetString(bytes).TrimStart('\uFEFF');

        List<Dictionary<string, object?>> entries;
        try
        {
            entries = ParseCamt053(content, config.EnforceBalanceChain);
        }
        catch (Exception ex)
        {
            nodeContext.Error($"Failed to parse camt.053 XML: {ex.Message}");
            return;
        }

        nodeContext.Info($"Parsed {entries.Count} entries from camt.053 statement");

        dataContext.Set(config.TargetPath, entries, config.DocumentMode,
            config.TargetValueKind, config.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Parses a camt.053.001.02 XML document into a list of normalized booking objects, one per Ntry.
    /// Namespace-agnostic (matches by local element name). Pure and side-effect free — unit-testable.
    /// When <paramref name="enforceBalanceChain"/> is true, each statement's PRCD + Σ bookings = CLBD is
    /// verified and a <see cref="FormatException"/> is thrown on mismatch (completeness hard stop).
    /// </summary>
    internal static List<Dictionary<string, object?>> ParseCamt053(string xml, bool enforceBalanceChain = true)
    {
        var doc = XDocument.Parse(xml);
        var root = doc.Root ?? throw new FormatException("Empty XML document");

        var result = new List<Dictionary<string, object?>>();

        foreach (var stmt in Descendants(root, "Stmt"))
        {
            var acct = Element(stmt, "Acct");
            var accountIban = Value(Element(Element(acct, "Id"), "IBAN"));
            var accountCurrency = Value(Element(acct, "Ccy"));
            var lglSeqNb = Value(Element(stmt, "LglSeqNb"));

            var position = 0;
            var stmtSum = 0d;
            foreach (var ntry in Elements(stmt, "Ntry"))
            {
                position++;

                var amtEl = Element(ntry, "Amt");
                var amountRaw = Value(amtEl);
                var currency = Attr(amtEl, "Ccy") ?? accountCurrency;
                var cdtDbtInd = Value(Element(ntry, "CdtDbtInd")); // CRDT | DBIT
                var isCredit = string.Equals(cdtDbtInd, "CRDT", StringComparison.OrdinalIgnoreCase);

                double amount = 0d;
                if (double.TryParse(amountRaw, NumberStyles.Any, CultureInfo.InvariantCulture, out var parsed))
                {
                    amount = isCredit ? parsed : -parsed;
                }

                stmtSum += amount;

                var acctSvcrRef = Value(Element(ntry, "AcctSvcrRef"));

                // Composite dedup key: IBAN|AcctSvcrRef when a real reference exists,
                // else IBAN|LglSeqNb|position (stable within one delivery frequency).
                string transactionId;
                if (!string.IsNullOrWhiteSpace(acctSvcrRef) &&
                    !string.Equals(acctSvcrRef, "NOTPROVIDED", StringComparison.OrdinalIgnoreCase))
                {
                    transactionId = $"{accountIban}|{acctSvcrRef}";
                }
                else
                {
                    transactionId = $"{accountIban}|{lglSeqNb}|{position}";
                }

                // First TxDtls carries party/reference detail (batch bookings have several; the
                // Ntry-level Amt is the single account movement we import).
                var ntryDtls = Element(ntry, "NtryDtls");
                var txDtls = Elements(ntryDtls, "TxDtls").FirstOrDefault();
                var refs = Element(txDtls, "Refs");

                // Counterparty depends on direction: for a debit we paid the creditor, for a
                // credit we received from the debtor.
                var rltdPties = Element(txDtls, "RltdPties");
                var rltdAgts = Element(txDtls, "RltdAgts");
                var partyEl = isCredit ? Element(rltdPties, "Dbtr") : Element(rltdPties, "Cdtr");
                var partyAcctEl = isCredit ? Element(rltdPties, "DbtrAcct") : Element(rltdPties, "CdtrAcct");
                var partyAgtEl = isCredit ? Element(rltdAgts, "DbtrAgt") : Element(rltdAgts, "CdtrAgt");

                var counterpartName = Value(Element(partyEl, "Nm"));
                var counterpartIban = Value(Element(Element(partyAcctEl, "Id"), "IBAN"));
                var counterpartBic = Value(Element(Element(partyAgtEl, "FinInstnId"), "BIC"));

                var purpose = BuildPurpose(txDtls);

                // Payment reference: Raiffeisen carries NtryRef at entry level; else fall back to TxId.
                var paymentReference = Value(Element(ntry, "NtryRef"));
                if (string.IsNullOrWhiteSpace(paymentReference))
                {
                    paymentReference = Value(Element(refs, "TxId"));
                }

                var endToEndReference = Value(Element(refs, "EndToEndId"));
                var mandateReference = Value(Element(Element(refs, "MndtRltdInf"), "MndtId"));

                // SEPA creditor identifier (ESDD direct debits), best-effort.
                var creditorId = FindCreditorSchemeId(rltdPties);

                // Bank transaction code (ESCT/ESDD/CWDL/STDO/...); read downstream as BANKTXCODE.
                var bkTxCd = Element(ntry, "BkTxCd");
                var domn = Element(bkTxCd, "Domn");
                var fmly = Element(domn, "Fmly");
                var bankTransactionCode = Value(Element(fmly, "SubFmlyCd"));
                if (string.IsNullOrWhiteSpace(bankTransactionCode))
                {
                    bankTransactionCode = Value(Element(Element(bkTxCd, "Prtry"), "Cd"));
                }

                result.Add(new Dictionary<string, object?>
                {
                    ["transactionId"] = transactionId,
                    ["amount"] = amount,
                    ["currency"] = NullIfEmpty(currency),
                    ["bookingDate"] = ParseDate(EntryDate(ntry, "BookgDt")),
                    ["valueDate"] = ParseDate(EntryDate(ntry, "ValDt")),
                    ["direction"] = isCredit ? 0 : 1, // 0=Credit, 1=Debit
                    ["counterpartName"] = NullIfEmpty(counterpartName),
                    ["counterpartIban"] = NullIfEmpty(counterpartIban),
                    ["counterpartBic"] = NullIfEmpty(counterpartBic),
                    ["purpose"] = NullIfEmpty(purpose),
                    ["paymentReference"] = NullIfEmpty(paymentReference),
                    ["endToEndReference"] = NullIfEmpty(endToEndReference),
                    ["mandateReference"] = NullIfEmpty(mandateReference),
                    ["creditorId"] = NullIfEmpty(creditorId),
                    ["bankTransactionCode"] = NullIfEmpty(bankTransactionCode),
                    ["accountIban"] = NullIfEmpty(accountIban),
                    ["lglSeqNb"] = NullIfEmpty(lglSeqNb),
                    ["position"] = position
                });
            }

            // Completeness hard stop (Begleitunterlage §4.2): opening balance (PRCD) plus the signed
            // sum of this statement's bookings must equal the closing balance (CLBD). A mismatch means
            // the statement is incomplete — abort the whole import rather than persist partial data.
            if (enforceBalanceChain)
            {
                var prcd = ReadBalance(stmt, "PRCD");
                var clbd = ReadBalance(stmt, "CLBD");
                if (prcd.HasValue && clbd.HasValue)
                {
                    var computed = prcd.Value + stmtSum;
                    if (Math.Abs(computed - clbd.Value) > 0.005d)
                    {
                        throw new FormatException(
                            $"camt.053 balance-chain mismatch on statement '{lglSeqNb}' (IBAN {accountIban}): " +
                            $"opening {prcd.Value:0.#####} + bookings {stmtSum:0.#####} = {computed:0.#####}, " +
                            $"but closing balance is {clbd.Value:0.#####} (diff {computed - clbd.Value:0.#####}). " +
                            "Statement is incomplete — import aborted.");
                    }
                }
            }
        }

        return result;
    }

    /// <summary>Reads a signed statement balance by code (e.g. PRCD/CLBD); sign from CdtDbtInd. Null if absent/unparseable.</summary>
    private static double? ReadBalance(XElement stmt, string code)
    {
        foreach (var bal in Elements(stmt, "Bal"))
        {
            var c = Value(Element(Element(Element(bal, "Tp"), "CdOrPrtry"), "Cd"));
            if (!string.Equals(c, code, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!double.TryParse(Value(Element(bal, "Amt")), NumberStyles.Any, CultureInfo.InvariantCulture, out var v))
            {
                return null;
            }

            var ind = Value(Element(bal, "CdtDbtInd"));
            return string.Equals(ind, "CRDT", StringComparison.OrdinalIgnoreCase) ? v : -v;
        }

        return null;
    }

    /// <summary>Concatenates unstructured and structured remittance info into one purpose string.</summary>
    private static string BuildPurpose(XElement? txDtls)
    {
        var rmtInf = Element(txDtls, "RmtInf");
        if (rmtInf == null)
        {
            return string.Empty;
        }

        var parts = new List<string>();
        parts.AddRange(Elements(rmtInf, "Ustrd").Select(e => e.Value.Trim()).Where(s => s.Length > 0));

        foreach (var strd in Elements(rmtInf, "Strd"))
        {
            var refText = Value(Element(Element(strd, "CdtrRefInf"), "Ref"));
            if (!string.IsNullOrWhiteSpace(refText))
            {
                parts.Add(refText.Trim());
            }
        }

        return string.Join(" ", parts);
    }

    /// <summary>Finds a SEPA creditor scheme identifier anywhere under the related parties, best-effort.</summary>
    private static string FindCreditorSchemeId(XElement? rltdPties)
    {
        if (rltdPties == null)
        {
            return string.Empty;
        }

        var scheme = DescendantsLocal(rltdPties, "CdtrSchmeId").FirstOrDefault();
        if (scheme == null)
        {
            return string.Empty;
        }

        return DescendantsLocal(scheme, "Id")
            .Select(id => id.Value.Trim())
            .FirstOrDefault(v => v.Length > 0) ?? string.Empty;
    }

    private static string EntryDate(XElement? ntry, string wrapper)
    {
        var w = Element(ntry, wrapper);
        var dt = Value(Element(w, "Dt"));
        return !string.IsNullOrWhiteSpace(dt) ? dt : Value(Element(w, "DtTm"));
    }

    private static object? ParseDate(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
        {
            return null;
        }

        if (DateTime.TryParse(raw, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var dt))
        {
            return dt.ToString("yyyy-MM-ddTHH:mm:ssZ", CultureInfo.InvariantCulture);
        }

        return raw;
    }

    // --- namespace-agnostic XElement helpers (match by local name) ---

    private static IEnumerable<XElement> Descendants(XElement el, string localName)
        => el.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> DescendantsLocal(XElement el, string localName)
        => el.Descendants().Where(e => e.Name.LocalName == localName);

    private static IEnumerable<XElement> Elements(XElement? el, string localName)
        => el == null ? Enumerable.Empty<XElement>() : el.Elements().Where(e => e.Name.LocalName == localName);

    private static XElement? Element(XElement? el, string localName)
        => el?.Elements().FirstOrDefault(e => e.Name.LocalName == localName);

    private static string Value(XElement? el) => el?.Value.Trim() ?? string.Empty;

    private static string? Attr(XElement? el, string localName)
        => el?.Attributes().FirstOrDefault(a => a.Name.LocalName == localName)?.Value;

    private static object? NullIfEmpty(string? s) => string.IsNullOrWhiteSpace(s) ? null : s;

    private static Encoding GetEncoding(string encodingName)
    {
        return encodingName.ToLowerInvariant() switch
        {
            "utf-16le" or "utf-16" => Encoding.Unicode,
            "utf-16be" => Encoding.BigEndianUnicode,
            "utf-32" => Encoding.UTF32,
            "ascii" => Encoding.ASCII,
            "latin1" or "iso-8859-1" => Encoding.Latin1,
            _ => Encoding.UTF8
        };
    }
}
