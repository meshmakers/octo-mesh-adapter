using System.Globalization;
using System.Numerics;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Xml.Linq;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Builds an ISO 20022 <c>pain.001.001.03</c> SEPA credit-transfer batch (Austrian
/// variant) from an array of payment items and writes it back as a base64-encoded
/// UTF-8 XML string, ready for a following <c>CreateFileSystemUpdate@1</c>.
/// The app never executes the payment — the file is imported and approved by the
/// user in their e-banking (SCA stays at the bank).
/// </summary>
[NodeConfiguration(typeof(BuildSepaCreditTransferNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public partial class BuildSepaCreditTransferNode(NodeDelegate next) : IPipelineNode
{
    private static readonly XNamespace Ns = "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03";
    private static readonly XNamespace Xsi = "http://www.w3.org/2001/XMLSchema-instance";

    private const string SchemaLocation =
        "urn:iso:std:iso:20022:tech:xsd:pain.001.001.03 ISO.pain.001.001.03.austrian.004.Korrigendum.xsd";

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<BuildSepaCreditTransferNodeConfiguration>();

        // --- resolve debtor + header (literal or JSONPath) ---
        var debtorName = Resolve(dataContext, config.DebtorName, config.DebtorNamePath)
                         ?? throw new PipelineNodeExecutionException("Debtor name is not set.");
        var debtorIban = Normalize(Resolve(dataContext, config.DebtorIban, config.DebtorIbanPath))
                         ?? throw new PipelineNodeExecutionException("Debtor IBAN is not set.");
        // Debtor BIC is optional: a BankAccount does not store one. When absent the
        // debtor agent is emitted as Othr/Id=NOTPROVIDED (valid in the Austrian schema).
        var debtorBic = Normalize(Resolve(dataContext, config.DebtorBic, config.DebtorBicPath));
        var msgId = Resolve(dataContext, config.MessageId, config.MessageIdPath)
                    ?? "MM-SEPA-" + DateTime.UtcNow.ToString("yyyyMMddHHmmssfff", CultureInfo.InvariantCulture);
        var pmtInfId = Resolve(dataContext, config.PaymentInformationId, config.PaymentInformationIdPath)
                       ?? msgId;
        var reqExecDate = Resolve(dataContext, config.RequestedExecutionDate, config.RequestedExecutionDatePath)
                          ?? DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
        var creDtTm = string.IsNullOrWhiteSpace(config.CreationDateTime)
            ? DateTime.UtcNow.ToString("yyyy-MM-ddTHH:mm:ss", CultureInfo.InvariantCulture)
            : config.CreationDateTime;

        var errors = new List<string>();
        if (!IsIbanValid(debtorIban))
        {
            errors.Add($"Debtor IBAN is invalid: {debtorIban}");
        }

        if (debtorBic != null && !BicRegex().IsMatch(debtorBic))
        {
            errors.Add($"Debtor BIC is invalid: {debtorBic}");
        }

        // --- read and validate the payment items ---
        var items = dataContext.GetArray<JsonNode>(config.Path)?.Where(n => n != null).ToList();
        if (items == null || items.Count == 0)
        {
            throw new PipelineNodeExecutionException($"No payment items found at {config.Path}.");
        }

        var payments = new List<Payment>();
        for (var i = 0; i < items.Count; i++)
        {
            var o = items[i]!.AsObject();
            var tag = $"Payment {i + 1}";
            var name = GetString(o, "recipientName", "name");
            var iban = Normalize(GetString(o, "recipientIban", "iban"));
            var bic = Normalize(GetString(o, "recipientBic", "bic"));
            var currency = GetString(o, "currency") ?? "EUR";
            var endToEndId = GetString(o, "endToEndId") ?? "NOTPROVIDED";
            var instructionId = GetString(o, "instructionId");
            var remittance = GetString(o, "remittance", "remittanceUnstructured", "ustrd");
            var creditorRef = GetString(o, "creditorReference", "structuredReference", "ref");
            var purposeCode = GetString(o, "purposeCode", "purpose");
            var executionDate = DatePart(GetString(o, "executionDate", "requestedExecutionDate"));
            var amount = GetDecimal(o, "amount");

            if (string.IsNullOrWhiteSpace(name))
            {
                errors.Add($"{tag}: recipient name is missing.");
            }

            if (iban == null || !IsIbanValid(iban))
            {
                errors.Add($"{tag} ({name}): recipient IBAN is invalid: {iban}");
            }

            if (bic != null && !BicRegex().IsMatch(bic))
            {
                errors.Add($"{tag} ({name}): recipient BIC is invalid: {bic}");
            }

            if (amount is not { } amt || Round2(amt) <= 0m)
            {
                errors.Add($"{tag} ({name}): amount must be > 0: {amount?.ToString(CultureInfo.InvariantCulture)}");
            }

            if (!string.Equals(currency, "EUR", StringComparison.Ordinal))
            {
                errors.Add($"{tag} ({name}): only EUR is allowed for SEPA: {currency}");
            }

            if (!string.IsNullOrEmpty(remittance) && !string.IsNullOrEmpty(creditorRef))
            {
                errors.Add($"{tag} ({name}): use either unstructured remittance OR a structured reference, not both.");
            }

            if (!string.IsNullOrEmpty(remittance) && remittance!.Length > 140)
            {
                errors.Add($"{tag} ({name}): remittance exceeds 140 characters ({remittance.Length}).");
            }

            payments.Add(new Payment(name ?? string.Empty, iban ?? string.Empty, bic,
                amount.HasValue ? Round2(amount.Value) : 0m, currency, endToEndId, instructionId,
                remittance, creditorRef, purposeCode, executionDate));
        }

        if (errors.Count > 0)
        {
            throw new PipelineNodeExecutionException(
                "SEPA credit transfer validation failed:" + Environment.NewLine + " - " +
                string.Join(Environment.NewLine + " - ", errors));
        }

        var total = payments.Aggregate(0m, (acc, p) => acc + p.Amount);
        var nb = payments.Count.ToString(CultureInfo.InvariantCulture);
        var ctrl = total.ToString("F2", CultureInfo.InvariantCulture);

        var debtorAddressLines = SplitLines(config.DebtorAddressLines);
        var xml = BuildDocument(debtorName, debtorIban, debtorBic, config.DebtorCurrency,
            config.DebtorCountry, debtorAddressLines, msgId, creDtTm, pmtInfId, reqExecDate,
            config.BatchBooking, config.InitiatingPartyOrgId, payments, nb, ctrl);

        var bytes = Encoding.UTF8.GetBytes(xml);
        nodeContext.Debug($"Built SEPA pain.001.001.03 batch: {payments.Count} transactions, control sum {ctrl} " +
                          $"({bytes.Length} bytes).");

        dataContext.Set(config.TargetPath, Convert.ToBase64String(bytes),
            config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);

        if (!string.IsNullOrEmpty(config.ContentLengthTargetPath))
        {
            dataContext.Set(config.ContentLengthTargetPath, (long)bytes.Length,
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
        }

        if (!string.IsNullOrEmpty(config.TransactionCountTargetPath))
        {
            dataContext.Set(config.TransactionCountTargetPath, (long)payments.Count,
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
        }

        if (!string.IsNullOrEmpty(config.ControlSumTargetPath))
        {
            dataContext.Set(config.ControlSumTargetPath, ctrl,
                config.DocumentMode, config.TargetValueKind, config.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private static string BuildDocument(string debtorName, string debtorIban, string? debtorBic,
        string debtorCurrency, string? debtorCountry, IReadOnlyList<string> debtorAddressLines,
        string msgId, string creDtTm, string pmtInfId, string reqExecDate, bool batchBooking,
        string? initiatingPartyOrgId, IReadOnlyList<Payment> payments, string nb, string ctrl)
    {
        var initgParty = new XElement(Ns + "InitgPty", El("Nm", debtorName));
        if (!string.IsNullOrWhiteSpace(initiatingPartyOrgId))
        {
            initgParty.Add(new XElement(Ns + "Id",
                new XElement(Ns + "OrgId",
                    new XElement(Ns + "Othr", El("Id", initiatingPartyOrgId)))));
        }

        var grpHdr = new XElement(Ns + "GrpHdr",
            El("MsgId", msgId),
            El("CreDtTm", creDtTm),
            El("NbOfTxs", nb),
            El("CtrlSum", ctrl),
            initgParty);

        var cstmr = new XElement(Ns + "CstmrCdtTrfInitn", grpHdr);

        // Group payments by requested execution date — a pain.001 PmtInf carries a
        // single ReqdExctnDt, so each distinct date becomes its own PmtInf block
        // (all under the same debtor). Input order is preserved.
        var groups = new List<(string Date, List<Payment> Items)>();
        foreach (var p in payments)
        {
            var date = string.IsNullOrWhiteSpace(p.ExecutionDate) ? reqExecDate : p.ExecutionDate!;
            var grp = groups.FirstOrDefault(g => g.Date == date);
            if (grp.Items == null)
            {
                grp = (date, new List<Payment>());
                groups.Add(grp);
            }

            grp.Items.Add(p);
        }

        var idx = 0;
        foreach (var (date, items) in groups)
        {
            idx++;
            var groupTotal = items.Aggregate(0m, (acc, x) => acc + x.Amount);
            var pmtInf = new XElement(Ns + "PmtInf",
                El("PmtInfId", groups.Count > 1 ? $"{pmtInfId}-{idx}" : pmtInfId),
                El("PmtMtd", "TRF"),
                El("BtchBookg", batchBooking ? "true" : "false"),
                El("NbOfTxs", items.Count.ToString(CultureInfo.InvariantCulture)),
                El("CtrlSum", groupTotal.ToString("F2", CultureInfo.InvariantCulture)),
                new XElement(Ns + "PmtTpInf", new XElement(Ns + "SvcLvl", El("Cd", "SEPA"))),
                El("ReqdExctnDt", date),
                Party("Dbtr", debtorName, debtorCountry, debtorAddressLines),
                new XElement(Ns + "DbtrAcct",
                    new XElement(Ns + "Id", El("IBAN", debtorIban)),
                    El("Ccy", debtorCurrency)),
                new XElement(Ns + "DbtrAgt", new XElement(Ns + "FinInstnId",
                    string.IsNullOrWhiteSpace(debtorBic)
                        // No BIC on file (a BankAccount stores none) — Othr/Id=NOTPROVIDED
                        // is the schema-valid IBAN-only form for the debtor agent.
                        ? new XElement(Ns + "Othr", El("Id", "NOTPROVIDED"))
                        : El("BIC", debtorBic))),
                El("ChrgBr", "SLEV"));

            foreach (var p in items)
            {
                pmtInf.Add(CreateTx(p));
            }

            cstmr.Add(pmtInf);
        }

        var root = new XElement(Ns + "Document",
            new XAttribute(XNamespace.Xmlns + "xsi", Xsi.NamespaceName),
            new XAttribute(Xsi + "schemaLocation", SchemaLocation),
            cstmr);

        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), root);
        using var sw = new Utf8StringWriter();
        doc.Save(sw);
        return sw.ToString();
    }

    private static XElement CreateTx(Payment p)
    {
        var pmtId = new XElement(Ns + "PmtId");
        if (!string.IsNullOrWhiteSpace(p.InstructionId))
        {
            pmtId.Add(El("InstrId", p.InstructionId));
        }

        pmtId.Add(El("EndToEndId", string.IsNullOrWhiteSpace(p.EndToEndId) ? "NOTPROVIDED" : p.EndToEndId));

        var tx = new XElement(Ns + "CdtTrfTxInf",
            pmtId,
            new XElement(Ns + "Amt",
                new XElement(Ns + "InstdAmt",
                    new XAttribute("Ccy", p.Currency),
                    p.Amount.ToString("F2", CultureInfo.InvariantCulture))));

        if (!string.IsNullOrWhiteSpace(p.Bic))
        {
            tx.Add(new XElement(Ns + "CdtrAgt", new XElement(Ns + "FinInstnId", El("BIC", p.Bic))));
        }

        tx.Add(Party("Cdtr", p.Name, null, Array.Empty<string>()));
        tx.Add(new XElement(Ns + "CdtrAcct", new XElement(Ns + "Id", El("IBAN", p.Iban))));

        if (!string.IsNullOrWhiteSpace(p.PurposeCode))
        {
            tx.Add(new XElement(Ns + "Purp", El("Cd", p.PurposeCode)));
        }

        if (!string.IsNullOrWhiteSpace(p.CreditorReference))
        {
            // Austrian variant requires Tp (SCOR) BEFORE Ref.
            tx.Add(new XElement(Ns + "RmtInf",
                new XElement(Ns + "Strd",
                    new XElement(Ns + "CdtrRefInf",
                        new XElement(Ns + "Tp", new XElement(Ns + "CdOrPrtry", El("Cd", "SCOR"))),
                        El("Ref", p.CreditorReference)))));
        }
        else if (!string.IsNullOrWhiteSpace(p.Remittance))
        {
            tx.Add(new XElement(Ns + "RmtInf", El("Ustrd", p.Remittance)));
        }

        return tx;
    }

    private static XElement El(string name, string value) => new(Ns + name, value);

    private static XElement Party(string tag, string name, string? country, IReadOnlyList<string> addressLines)
    {
        var party = new XElement(Ns + tag, El("Nm", name));
        // Austrian variant: PstlAdr only when at least one AdrLine is present
        // (country alone is not valid).
        if (addressLines.Count > 0)
        {
            var adr = new XElement(Ns + "PstlAdr");
            if (!string.IsNullOrWhiteSpace(country))
            {
                adr.Add(El("Ctry", country));
            }

            foreach (var line in addressLines)
            {
                adr.Add(El("AdrLine", line));
            }

            party.Add(adr);
        }

        return party;
    }

    private static string? Resolve(IDataContext dataContext, string? literal, string? path)
    {
        if (!string.IsNullOrWhiteSpace(literal))
        {
            return literal;
        }

        return string.IsNullOrWhiteSpace(path) ? null : dataContext.Get<string>(path);
    }

    private static IReadOnlyList<string> SplitLines(string? semicolonSeparated)
    {
        if (string.IsNullOrWhiteSpace(semicolonSeparated))
        {
            return Array.Empty<string>();
        }

        return semicolonSeparated.Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string? Normalize(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Replace(" ", "").ToUpperInvariant();

    private static string? GetString(JsonObject o, params string[] names)
    {
        foreach (var name in names)
        {
            foreach (var kvp in o)
            {
                if (string.Equals(kvp.Key, name, StringComparison.OrdinalIgnoreCase) && kvp.Value != null)
                {
                    var s = kvp.Value.ToString();
                    return string.IsNullOrWhiteSpace(s) ? null : s;
                }
            }
        }

        return null;
    }

    private static decimal? GetDecimal(JsonObject o, params string[] names)
    {
        var s = GetString(o, names);
        if (s == null)
        {
            return null;
        }

        return decimal.TryParse(s, NumberStyles.Number, CultureInfo.InvariantCulture, out var d) ? d : null;
    }

    private static decimal Round2(decimal value) => Math.Round(value, 2, MidpointRounding.AwayFromZero);

    private static bool IsIbanValid(string? iban)
    {
        if (string.IsNullOrWhiteSpace(iban))
        {
            return false;
        }

        iban = iban.Replace(" ", "").ToUpperInvariant();
        if (!IbanRegex().IsMatch(iban))
        {
            return false;
        }

        var rearranged = iban[4..] + iban[..4];
        var sb = new StringBuilder(rearranged.Length * 2);
        foreach (var c in rearranged)
        {
            if (char.IsLetter(c))
            {
                sb.Append((c - 'A' + 10).ToString(CultureInfo.InvariantCulture));
            }
            else
            {
                sb.Append(c);
            }
        }

        return BigInteger.TryParse(sb.ToString(), out var value) && value % 97 == 1;
    }

    [GeneratedRegex("^[A-Z]{2}[0-9]{2}[A-Z0-9]{1,30}$")]
    private static partial Regex IbanRegex();

    [GeneratedRegex("^[A-Z]{6}[A-Z0-9]{2}([A-Z0-9]{3})?$")]
    private static partial Regex BicRegex();

    /// <summary>Trims an ISO date-time (e.g. "2026-08-05T00:00:00Z") to its date part "2026-08-05".</summary>
    private static string? DatePart(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var t = value.IndexOf('T');
        return t >= 10 ? value[..t] : value;
    }

    private sealed record Payment(
        string Name,
        string Iban,
        string? Bic,
        decimal Amount,
        string Currency,
        string EndToEndId,
        string? InstructionId,
        string? Remittance,
        string? CreditorReference,
        string? PurposeCode,
        string? ExecutionDate);

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
