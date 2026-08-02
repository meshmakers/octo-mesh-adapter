using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for <c>BuildSepaCreditTransfer@1</c>. Builds an ISO 20022
/// <c>pain.001.001.03</c> SEPA credit-transfer batch (Austrian variant,
/// <c>ISO.pain.001.001.03.austrian.004.Korrigendum</c>) from an array of payment
/// items and writes it as a base64-encoded UTF-8 XML string to
/// <see cref="TargetPathNodeConfiguration.TargetPath"/> — ready for a following
/// <c>CreateFileSystemUpdate@1</c> (contentType <c>application/xml</c>).
///
/// Each element read from <see cref="SourceTargetPathNodeConfiguration.Path"/> is a
/// JSON object with these fields (camelCase, case-insensitive):
/// <list type="bullet">
/// <item><c>recipientName</c> (required)</item>
/// <item><c>recipientIban</c> (required, IBAN mod-97 checked)</item>
/// <item><c>recipientBic</c> (optional)</item>
/// <item><c>amount</c> (required, &gt; 0)</item>
/// <item><c>currency</c> (optional, default <c>EUR</c>; SEPA requires EUR)</item>
/// <item><c>endToEndId</c> (optional, default <c>NOTPROVIDED</c>)</item>
/// <item><c>instructionId</c> (optional)</item>
/// <item><c>remittance</c> (optional, unstructured <c>Ustrd</c>, max 140 chars)</item>
/// <item><c>creditorReference</c> (optional, structured RF reference; mutually exclusive with <c>remittance</c>)</item>
/// <item><c>purposeCode</c> (optional, e.g. <c>TAXS</c>)</item>
/// </list>
/// </summary>
[NodeName("BuildSepaCreditTransfer", 1)]
public record BuildSepaCreditTransferNodeConfiguration : SourceTargetPathNodeConfiguration
{
    // --- Debtor (the paying company account) ---

    /// <summary>Debtor (ordering party) name.</summary>
    [PropertyGroup("Debtor", 0)]
    public string? DebtorName { get; set; }

    /// <summary>JSONPath to the debtor name.</summary>
    [PropertyGroup("Debtor", 1, "jsonpath")]
    public string? DebtorNamePath { get; set; }

    /// <summary>Debtor IBAN.</summary>
    [PropertyGroup("Debtor", 2)]
    public string? DebtorIban { get; set; }

    /// <summary>JSONPath to the debtor IBAN.</summary>
    [PropertyGroup("Debtor", 3, "jsonpath")]
    public string? DebtorIbanPath { get; set; }

    /// <summary>Debtor agent BIC.</summary>
    [PropertyGroup("Debtor", 4)]
    public string? DebtorBic { get; set; }

    /// <summary>JSONPath to the debtor BIC.</summary>
    [PropertyGroup("Debtor", 5, "jsonpath")]
    public string? DebtorBicPath { get; set; }

    /// <summary>Debtor account currency (default <c>EUR</c>).</summary>
    [PropertyGroup("Debtor", 6)]
    public string DebtorCurrency { get; set; } = "EUR";

    /// <summary>Optional debtor country code (only emitted together with address lines).</summary>
    [PropertyGroup("Debtor", 7)]
    public string? DebtorCountry { get; set; }

    /// <summary>
    /// Optional debtor postal address lines, semicolon-separated. Only emitted when
    /// at least one line is present (the Austrian schema requires an <c>AdrLine</c>
    /// inside <c>PstlAdr</c>; country alone is invalid).
    /// </summary>
    [PropertyGroup("Debtor", 8)]
    public string? DebtorAddressLines { get; set; }

    // --- Batch header ---

    /// <summary>Message id (<c>GrpHdr/MsgId</c>).</summary>
    [PropertyGroup("Header", 0)]
    public string? MessageId { get; set; }

    /// <summary>JSONPath to the message id.</summary>
    [PropertyGroup("Header", 1, "jsonpath")]
    public string? MessageIdPath { get; set; }

    /// <summary>Payment information id (<c>PmtInf/PmtInfId</c>). Defaults to the message id.</summary>
    [PropertyGroup("Header", 2)]
    public string? PaymentInformationId { get; set; }

    /// <summary>JSONPath to the payment information id.</summary>
    [PropertyGroup("Header", 3, "jsonpath")]
    public string? PaymentInformationIdPath { get; set; }

    /// <summary>Requested execution date, <c>yyyy-MM-dd</c> (<c>PmtInf/ReqdExctnDt</c>).</summary>
    [PropertyGroup("Header", 4)]
    public string? RequestedExecutionDate { get; set; }

    /// <summary>JSONPath to the requested execution date.</summary>
    [PropertyGroup("Header", 5, "jsonpath")]
    public string? RequestedExecutionDatePath { get; set; }

    /// <summary>
    /// Creation date-time, ISO 8601 (<c>GrpHdr/CreDtTm</c>). When empty, the current
    /// UTC time is used. Settable mainly for deterministic tests.
    /// </summary>
    [PropertyGroup("Header", 6)]
    public string? CreationDateTime { get; set; }

    /// <summary>Batch booking flag (<c>PmtInf/BtchBookg</c>, default <c>true</c>).</summary>
    [PropertyGroup("Header", 7)]
    public bool BatchBooking { get; set; } = true;

    /// <summary>
    /// Optional initiating-party organisation id (<c>GrpHdr/InitgPty/Id/OrgId/Othr/Id</c>).
    /// BMD puts the debtor IBAN here; not required.
    /// </summary>
    [PropertyGroup("Header", 8)]
    public string? InitiatingPartyOrgId { get; set; }

    // --- Output ---

    /// <summary>
    /// Optional path to write the produced XML's byte length to (as a long), for
    /// feeding a following <c>CreateFileSystemUpdate@1</c>.
    /// </summary>
    [PropertyGroup("Data", 0, "jsonpath")]
    public string? ContentLengthTargetPath { get; set; }

    /// <summary>Optional path to write the number of transactions to (as a long).</summary>
    [PropertyGroup("Data", 1, "jsonpath")]
    public string? TransactionCountTargetPath { get; set; }

    /// <summary>Optional path to write the control sum (total amount) to (as a string).</summary>
    [PropertyGroup("Data", 2, "jsonpath")]
    public string? ControlSumTargetPath { get; set; }
}
