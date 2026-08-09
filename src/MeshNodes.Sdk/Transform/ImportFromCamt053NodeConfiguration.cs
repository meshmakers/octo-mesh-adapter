using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Transform;

/// <summary>
/// Configuration for importing bank transactions from a camt.053.001.02 XML bank statement.
/// Parses namespace-agnostically (by local element name), so both the ISO standard namespace
/// (urn:iso:std:iso:20022:tech:xsd:camt.053.001.02) and the Austrian STUZZA/APC variant
/// (ISO:camt.053.001.02:APC:STUZZA:payments:003) are handled by the same node. Emits one
/// normalized entry object per booking (Ntry) into <see cref="TargetPathNodeConfiguration.TargetPath"/>.
/// </summary>
[NodeName("ImportFromCamt053", 1)]
public record ImportFromCamt053NodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Index of the file in $.files[] array (set by FromHttpRequest@1 for multipart/form-data uploads).
    /// </summary>
    [PropertyGroup("General", 1)]
    public int FileIndex { get; set; }

    /// <summary>
    /// File encoding (e.g. utf-8, utf-16le). camt.053 is UTF-8 by spec; overridable for odd exports.
    /// </summary>
    [PropertyGroup("Options", 0)]
    public string Encoding { get; set; } = "utf-8";

    /// <summary>
    /// When true (default), each statement's balance chain is verified: opening balance (PRCD) plus the
    /// signed sum of its bookings must equal the closing balance (CLBD). On mismatch the whole import is
    /// aborted (hard stop) so an incomplete statement is never partially imported — the completeness
    /// guarantee the tax advisor relies on. Set false to import without the check (statements missing
    /// PRCD/CLBD are skipped either way).
    /// </summary>
    [PropertyGroup("Options", 1)]
    public bool EnforceBalanceChain { get; set; } = true;
}
