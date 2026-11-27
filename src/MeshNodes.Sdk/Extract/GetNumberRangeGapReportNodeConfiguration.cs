using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Extract;

/// <summary>
/// Configuration for the node that reads the gap report of a number range for one fiscal year: the numbers from the
/// range's start value to the fiscal year's current value that no released document carries, and those more than one
/// document carries.
/// </summary>
[NodeName("GetNumberRangeGapReport", 1)]
// ReSharper disable once ClassNeverInstantiated.Global
public record GetNumberRangeGapReportNodeConfiguration : TargetPathNodeConfiguration
{
    /// <summary>
    /// Identity this node runs as: <c>Caller</c> (default), <c>ServiceAccount</c> (the pipeline's
    /// service account with its full roles, even when a caller is present), or <c>System</c>
    /// (unfiltered, bypasses data permissions). The report counts only the documents this identity may read.
    /// </summary>
    [PropertyGroup("Execution", 100)]
    public NodeExecutionIdentity Identity { get; set; } = NodeExecutionIdentity.Caller;

    /// <summary>
    /// Well-known name of the number range (<c>System/AutoIncrement</c>): the <c>autoIncrementReference</c> of the
    /// document type's number attribute (use either NumberRange or NumberRangePath)
    /// </summary>
    [PropertyGroup("Range", 0)]
    public string? NumberRange { get; set; }

    /// <summary>
    /// Json path to the well-known name of the number range
    /// </summary>
    [PropertyGroup("Range", 1, "jsonpath")]
    public string? NumberRangePath { get; set; }

    /// <summary>
    /// The fiscal year (the calendar year it starts in); required for a range with fiscal-year periods, left empty
    /// for one without (use either FiscalYear or FiscalYearPath)
    /// </summary>
    [PropertyGroup("Range", 2)]
    public int? FiscalYear { get; set; }

    /// <summary>
    /// Json path to the fiscal year
    /// </summary>
    [PropertyGroup("Range", 3, "jsonpath")]
    public string? FiscalYearPath { get; set; }
}
