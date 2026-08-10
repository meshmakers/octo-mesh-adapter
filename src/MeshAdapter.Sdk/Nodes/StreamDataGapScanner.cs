using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Reads a windowed archive's row windows for a time range and turns them into a coverage report.
/// Shared by <c>GetStreamData@1</c>, which reports the gaps, and <c>AggregateStreamData@1</c>, which
/// uses them as a guard — so the two can never drift apart on the scan itself, the row cap, the
/// interval fallback or the overlap warning.
/// </summary>
internal static class StreamDataGapScanner
{
    /// <summary>
    /// Row cap for the coverage scan when none is configured. A year of quarter-hourly data is about
    /// 35 000 rows per entity, so this leaves room for a handful of entities over a long range while
    /// still bounding memory.
    /// </summary>
    internal const int DefaultMaxGapScanRows = 200_000;

    /// <summary>
    /// The settings the scan needs, independent of which node's configuration they came from.
    /// </summary>
    /// <param name="From">Start of the analysed window (UTC), inclusive.</param>
    /// <param name="To">End of the analysed window (UTC), exclusive.</param>
    /// <param name="ExpectedInterval">
    /// Interval for the counts, or null to fall back to the archive's declared period. Must be
    /// positive when set — the callers validate that.
    /// </param>
    /// <param name="MaxGapScanRows">Row cap, or null for <see cref="DefaultMaxGapScanRows" />.</param>
    /// <param name="RtIds">Entity scope; also the expected set, see <see cref="ScanAsync" />.</param>
    /// <param name="FieldFilters">The same filters the caller applies to its own query.</param>
    internal readonly record struct Request(
        DateTime From,
        DateTime To,
        TimeSpan? ExpectedInterval,
        int? MaxGapScanRows,
        IReadOnlyList<OctoObjectId>? RtIds,
        IReadOnlyList<FieldFilter>? FieldFilters);

    /// <summary>
    /// Runs the coverage scan and builds the report. Deliberately a query of its own rather than a
    /// reuse of the caller's: a data query's <c>Limit</c> / <c>Skip</c> / <c>Take</c> would hide rows
    /// and make the scan report gaps that are not there.
    /// <para>
    /// Only <c>window_start</c> is projected — <c>window_end</c> arrives as the row's timestamp and
    /// the well-known name sits on the row itself. The scan asks for one row over the cap so a
    /// truncated result is detectable instead of silently producing a wrong report.
    /// </para>
    /// </summary>
    internal static async Task<StreamDataGapReport> ScanAsync(IStreamDataRepository streamDataRepo,
        ArchiveSnapshot snapshot, OctoObjectId archiveRtId, Request request, INodeContext nodeContext)
    {
        var maxRows = request.MaxGapScanRows ?? DefaultMaxGapScanRows;

        // Clamped because int.MaxValue (the natural way to ask for "no cap") would wrap into a
        // negative limit and reach the storage layer as an invalid query.
        var scanLimit = maxRows == int.MaxValue ? int.MaxValue : maxRows + 1;

        var options = StreamDataQueryOptions.Create()
            .WithCkTypeId(snapshot.TargetCkTypeId)
            .WithColumns([StreamDataNodeHelpers.WindowStartColumn])
            .WithRtIds(request.RtIds)
            .WithTimeRange(request.From, request.To)
            .WithFieldFilters(request.FieldFilters)
            .WithLimit(scanLimit);

        StreamDataQueryResult result;
        try
        {
            result = await streamDataRepo.ExecuteQueryAsync(archiveRtId, options);
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(nodeContext,
                archiveRtId, ex);
        }

        if (result.Rows.Count > maxRows)
        {
            throw MeshAdapterPipelineExecutionException.GapScanRowLimitExceeded(nodeContext, maxRows);
        }

        // A configured ExpectedInterval is validated as positive by the caller, so only a missing or
        // non-positive archive period can land in the warning below.
        var interval = request.ExpectedInterval ?? snapshot.Period;
        if (interval is not { Ticks: > 0 })
        {
            nodeContext.Warning(
                "No interval known for the gap report — ExpectedInterval is unset and the archive " +
                "declares no usable period. Gaps are reported as time ranges; the interval counts " +
                "stay empty.");
        }

        // An entity that delivered nothing at all returns no rows and would be invisible. Where the
        // caller named the entities, the expected set is known and each missing one is reported as a
        // full-window gap instead.
        var expected = request.RtIds?.Select(id => ((OctoObjectId?)id, (string?)null));
        if (expected is null)
        {
            nodeContext.Debug(
                "Gap report covers only entities with at least one row in the range; an entity that " +
                "delivered nothing cannot be detected unless RtIds names it.");
        }

        var series = StreamDataGapAnalyzer.BuildSeries(result.Rows, expected);
        var report = StreamDataGapAnalyzer.Analyse(series, request.From, request.To, interval);

        if (report.Series.Any(s => s.HasOverlaps))
        {
            // Legal per the storage concept, but a sum over overlapping windows double-counts.
            nodeContext.Warning(
                $"Archive '{archiveRtId}' has overlapping windows in the analysed range for " +
                $"{report.Series.Count(s => s.HasOverlaps)} of {report.SeriesCount} series. " +
                "Aggregating over them counts the overlap more than once.");
        }

        nodeContext.Debug(
            $"Gap report: {report.SeriesWithGapsCount} of {report.SeriesCount} series incomplete.");

        return report;
    }
}
