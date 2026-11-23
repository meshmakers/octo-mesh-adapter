using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Finds the missing time ranges of many series in one windowed archive without a row limit — the
/// engine of <c>DetectStreamDataGaps@1</c>. It reads only the given archive and analyses the windows
/// with <see cref="StreamDataGapAnalyzer" />, the same rule set as the gap report of
/// <c>GetStreamData@1</c>, so both report the same ranges and counts for the same data.
/// <para>
/// <b>How it stays fast and unlimited.</b> The series are processed in batches. Per batch one coarse
/// COUNT of the windows per series and 24-hour bin anchored at <c>From</c> runs first
/// (<see cref="IStreamDataRepository.ExecuteDownsamplingQueryAsync" />, AB#7360). A bin whose count
/// equals the slots it holds is taken as complete and is not read; a bin without windows is missing
/// as a whole; only the other bins, and a period remainder shorter than a day, are read row by row
/// (only <c>window_start</c>, sliced by time and paged). Without a usable COUNT — the archive does
/// not declare the slot length as its <c>Period</c>, the period is shorter than a day, the slot does
/// not divide a day, <c>From</c> is not on a whole minute, or the storage refuses the bins (e.g.
/// windows across a bin boundary) — the whole batch is read row by row, which gives the same result,
/// only slower.
/// </para>
/// <para>
/// Known limit of the COUNT shortcut: a count stands for "every slot delivered" only when the
/// windows are one slot long, which is why the shortcut needs the archive's declared <c>Period</c> to
/// equal the slot length. An archive that declares that period but still stores windows of another
/// length (shorter, longer or overlapping ones) inside a bin whose window count happens to equal its
/// slot count hides a hole there: such a bin is not read, so the hole is neither seen nor flagged.
/// </para>
/// <para>
/// <b>Validity periods</b> (AB#9589). With <see cref="Request.Validity" /> each series is analysed once
/// per effective period (its periods merged and cut to [From, To)), so the ranges end exactly at the
/// validity boundaries. Reads skip what lies outside every period where that saves work: the COUNT of
/// a batch spans from the first to the last whole bin the union of the batch's periods touches, a bin
/// is read for a series only when one of its periods touches it (then whole), and the remaining row
/// reads cover only that union. Without validity periods every
/// series has the single period [From, To) and the reads are exactly those described above.
/// </para>
/// </summary>
internal sealed class StreamDataGapDetector
{
    /// <summary>Series per batch at most (fewer when the period has many days, see <see cref="MaxCountRowsPerQuery" />).</summary>
    internal const int DefaultMaxSeriesPerBatch = 1000;

    /// <summary>Rows per page of a row read; the time slices are cut so one page normally suffices.</summary>
    internal const int DefaultPageRows = 50_000;

    /// <summary>
    /// Bins x series one COUNT may return — the storage layer's cap for local-day bins per series
    /// (<c>MaxLocalDaySeriesRows</c> of the CrateDB repository).
    /// </summary>
    internal const int MaxCountRowsPerQuery = 100_000;

    /// <summary>Bins one COUNT may have (<c>DownsamplingBinAxis.MaxBins</c>).</summary>
    internal const int MaxCountBins = 10_000;

    /// <summary>Shortest time slice of a row read (one hour), so a tiny slot cannot multiply the queries.</summary>
    private const long MinSliceTicks = TimeSpan.TicksPerHour;

    /// <summary>The series identity column the COUNT groups by.</summary>
    private const string RtIdColumn = "rtId";

    private static readonly TimeSpan Bin = TimeSpan.FromDays(1);

    private readonly IStreamDataRepository _repository;
    private readonly ArchiveSnapshot _snapshot;
    private readonly OctoObjectId _archiveRtId;
    private readonly INodeContext _nodeContext;
    private readonly int _maxSeriesPerBatch;
    private readonly int _pageRows;

    /// <summary>What one detection needs.</summary>
    /// <param name="From">Start of the period (UTC), inclusive.</param>
    /// <param name="To">End of the period (UTC), exclusive; later than <paramref name="From" />.</param>
    /// <param name="Interval">Slot length, positive.</param>
    /// <param name="RtIds">The series to check.</param>
    /// <param name="FieldFilters">Filters a row must match to count as present.</param>
    /// <param name="Validity">
    /// Validity periods per series (AB#9589), or null to check the whole period for every series.
    /// When set, a series is checked only inside its periods cut to [From, To), a series without an
    /// entry is neither checked nor reported, and nothing outside every period is read.
    /// </param>
    internal readonly record struct Request(
        DateTime From,
        DateTime To,
        TimeSpan Interval,
        IReadOnlyList<OctoObjectId> RtIds,
        IReadOnlyList<FieldFilter>? FieldFilters,
        IReadOnlyDictionary<OctoObjectId, List<StreamDataValidityPeriods.Period>>? Validity = null);

    internal StreamDataGapDetector(IStreamDataRepository repository, ArchiveSnapshot snapshot,
        OctoObjectId archiveRtId, INodeContext nodeContext, int maxSeriesPerBatch = DefaultMaxSeriesPerBatch,
        int pageRows = DefaultPageRows)
    {
        _repository = repository;
        _snapshot = snapshot;
        _archiveRtId = archiveRtId;
        _nodeContext = nodeContext;
        _maxSeriesPerBatch = Math.Max(1, maxSeriesPerBatch);
        _pageRows = Math.Max(1, pageRows);
    }

    /// <summary>COUNT queries run.</summary>
    internal int CountQueries { get; private set; }

    /// <summary>Batches whose COUNT the storage refused, read row by row instead.</summary>
    internal int CountFallbacks { get; private set; }

    /// <summary>Row-read queries (pages) run.</summary>
    internal int RowQueries { get; private set; }

    /// <summary>Rows read by the row reads.</summary>
    internal long RowsRead { get; private set; }

    internal async Task<StreamDataGapDetectionResult> DetectAsync(Request request)
    {
        var series = request.RtIds
            .Distinct()
            .OrderBy(id => id.ToString(), StringComparer.Ordinal)
            .ToList();

        // The periods each series is checked in; without validity periods that is the whole period.
        var effective = new Dictionary<OctoObjectId, List<StreamDataGapAnalyzer.Window>>();
        if (request.Validity is { } validity)
        {
            var withoutPeriod = 0;
            foreach (var rtId in series)
            {
                if (validity.TryGetValue(rtId, out var periods))
                {
                    effective[rtId] = StreamDataValidityPeriods.Effective(periods, request.From, request.To);
                }
                else
                {
                    withoutPeriod++;
                }
            }

            if (withoutPeriod != 0)
            {
                _nodeContext.Info(
                    $"DetectStreamDataGaps: {withoutPeriod} of {series.Count} series have no validity period " +
                    "and are neither checked nor reported.");
            }

            // A series with an entry counts as checked; one whose periods lie outside [From, To) has
            // no slot to check and is never queried.
            series = series.Where(effective.ContainsKey).ToList();
        }
        else
        {
            var whole = new StreamDataGapAnalyzer.Window(request.From, request.To);
            foreach (var rtId in series)
            {
                effective[rtId] = [whole];
            }
        }

        var queried = series.Where(id => effective[id].Count != 0).ToList();

        var plan = CountPlan.Create(request, _snapshot.Period);
        var batchSize = plan.Usable
            ? Math.Max(1, Math.Min(_maxSeriesPerBatch, MaxCountRowsPerQuery / plan.Days))
            : _maxSeriesPerBatch;

        var withGaps = new List<StreamDataGapDetectionSeries>();
        var overlapping = new List<string>();
        long missingTotal = 0;

        for (var offset = 0; offset < queried.Count; offset += batchSize)
        {
            var batch = queried.GetRange(offset, Math.Min(batchSize, queried.Count - offset));
            var windows = batch.ToDictionary(id => id, _ => new HashSet<StreamDataGapAnalyzer.Window>());

            await CollectWindowsAsync(batch, windows, effective, request, plan);

            foreach (var rtId in batch)
            {
                var seriesWindows = windows[rtId].ToList();
                var ranges = new List<StreamDataGapDetectionRange>();
                var hasOverlaps = false;
                long missing = 0;

                // One analysis per effective period, so every range is cut at the validity
                // boundaries and a gap across a hole between two periods becomes two ranges. Without
                // validity periods this is the single analysis over [From, To) of AB#9588.
                foreach (var period in effective[rtId])
                {
                    var analysed = StreamDataGapAnalyzer.AnalyseSeries(
                        new StreamDataGapAnalyzer.Series { RtId = rtId, Windows = seriesWindows },
                        period.Start, period.End, request.Interval);

                    hasOverlaps |= analysed.HasOverlaps;
                    foreach (var gap in analysed.Gaps)
                    {
                        var count = gap.MissingIntervals ?? 0;
                        missing += count;
                        ranges.Add(new StreamDataGapDetectionRange
                        {
                            Start = gap.From,
                            End = gap.To,
                            MissingCount = count
                        });
                    }
                }

                if (hasOverlaps)
                {
                    overlapping.Add(rtId.ToString());
                }

                if (ranges.Count == 0)
                {
                    continue;
                }

                if (missing > int.MaxValue)
                {
                    throw MeshAdapterPipelineExecutionException.DetectGapsMissingCountTooLarge(_nodeContext,
                        rtId.ToString());
                }

                missingTotal += missing;
                withGaps.Add(new StreamDataGapDetectionSeries
                {
                    RtId = rtId.ToString(),
                    MissingCount = (int)missing,
                    HasOverlaps = hasOverlaps,
                    Ranges = ranges
                });
            }
        }

        if (overlapping.Count != 0)
        {
            // Legal per the storage concept and never counted twice here, but worth knowing: a sum
            // over such windows does double-count.
            _nodeContext.Warning(
                $"Archive '{_archiveRtId}' holds overlapping windows in the period for {overlapping.Count} " +
                $"series: {string.Join(", ", overlapping.Take(10))}{(overlapping.Count > 10 ? ", ..." : string.Empty)}. " +
                "They are counted once.");
        }

        _nodeContext.Debug(
            $"DetectStreamDataGaps: {withGaps.Count} of {series.Count} series with gaps; " +
            $"{CountQueries} count queries ({CountFallbacks} refused), {RowQueries} row queries, {RowsRead} rows read.");

        return new StreamDataGapDetectionResult
        {
            From = request.From,
            To = request.To,
            Interval = StreamDataGapDuration.ToIso8601(request.Interval),
            ExpectedCount = StreamDataGapAnalyzer.CountIntervals(request.To - request.From, request.Interval),
            SeriesCount = series.Count,
            SeriesWithGapsCount = withGaps.Count,
            MissingCount = missingTotal,
            Series = withGaps
        };
    }

    private async Task CollectWindowsAsync(IReadOnlyList<OctoObjectId> batch,
        Dictionary<OctoObjectId, HashSet<StreamDataGapAnalyzer.Window>> windows,
        IReadOnlyDictionary<OctoObjectId, List<StreamDataGapAnalyzer.Window>> effective, Request request,
        CountPlan plan)
    {
        // Only the union of the batch's effective periods is read: without validity periods that is
        // [From, To), with them nothing outside every period of the batch.
        var union = StreamDataValidityPeriods.Merge(batch.SelectMany(id => effective[id]));
        if (union.Count == 0)
        {
            return;
        }

        var rowReadFrom = request.From;

        if (plan.Usable)
        {
            // The whole bins the union touches; outside them nothing is counted.
            var firstBin = (int)Math.Min(plan.Days, (union[0].Start - request.From).Ticks / Bin.Ticks);
            var lastBin = (int)Math.Min(plan.Days, CeilDiv((union[^1].End - request.From).Ticks, Bin.Ticks));

            if (firstBin >= lastBin)
            {
                // The union lies wholly in the remainder after the last whole bin.
                rowReadFrom = plan.CountEnd;
            }
            else if (await TryCountAsync(batch, request, plan, firstBin, lastBin) is { } counts)
            {
                var partialBins = new SortedDictionary<DateTime, List<OctoObjectId>>();

                foreach (var rtId in batch)
                {
                    var periods = effective[rtId];
                    for (var day = firstBin; day < lastBin; day++)
                    {
                        var binStart = request.From + Bin * day;
                        if (!StreamDataValidityPeriods.Overlaps(periods, binStart, binStart + Bin))
                        {
                            // Outside every period of this series: never checked, so never read.
                            continue;
                        }

                        var count = counts.GetValueOrDefault((rtId, binStart));

                        if (count == plan.SlotsPerBin)
                        {
                            // Complete: stands in for the windows it holds, which are never read.
                            windows[rtId].Add(new StreamDataGapAnalyzer.Window(binStart, binStart + Bin));
                        }
                        else if (count != 0)
                        {
                            // Partly filled, or more windows than slots (overlaps): read row by row.
                            if (!partialBins.TryGetValue(binStart, out var ids))
                            {
                                ids = [];
                                partialBins[binStart] = ids;
                            }

                            ids.Add(rtId);
                        }
                        // count == 0: no window in the bin, the analysis reports it as missing.
                    }
                }

                foreach (var (binStart, ids) in partialBins)
                {
                    await ReadWindowsAsync(ids, binStart, binStart + Bin, request, windows);
                }

                rowReadFrom = plan.CountEnd;
            }
        }

        // The rest (the remainder after the counted bins, or everything when the count is not
        // usable or was refused), cut to the union of the batch's periods.
        foreach (var period in union)
        {
            var readFrom = period.Start > rowReadFrom ? period.Start : rowReadFrom;
            if (readFrom < period.End)
            {
                await ReadWindowsAsync(batch, readFrom, period.End, request, windows);
            }
        }
    }

    /// <summary>Division rounded up, for a non-negative dividend.</summary>
    private static long CeilDiv(long value, long divisor) => (value + divisor - 1) / divisor;

    /// <summary>
    /// Windows per series and bin, or null when the storage refused the bins (the caller then reads
    /// the batch row by row). Any other failure fails the node with the storage message.
    /// </summary>
    private async Task<Dictionary<(OctoObjectId RtId, DateTime Bin), long>?> TryCountAsync(
        IReadOnlyList<OctoObjectId> batch, Request request, CountPlan plan, int firstBin, int lastBin)
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(_snapshot.TargetCkTypeId)
            .WithAggregationColumns([new AggregationColumn(StreamDataNodeHelpers.WindowStartColumn, AggregationFunction.Count)])
            .WithGroupByColumns([RtIdColumn])
            .WithRtIds(batch)
            .WithFieldFilters(request.FieldFilters)
            // Whole bins only, so the bins stay anchored at From: without validity periods the
            // range is From..CountEnd as in AB#9588, with them only the bins the batch's periods touch.
            .WithTimeRange(request.From + Bin * firstBin, request.From + Bin * lastBin)
            // No zone: fixed 24-hour bins in UTC, starting at From's instant. Counting is by
            // instants, so a DST change in the caller's zone needs no special case.
            .WithBinOrigin(request.From.TimeOfDay)
            .WithBinInterval(Bin);

        StreamDataQueryResult result;
        try
        {
            CountQueries++;
            result = await _repository.ExecuteDownsamplingQueryAsync(_archiveRtId, options);
        }
        catch (DownsamplingBinOptionsInvalidException ex)
        {
            CountFallbacks++;
            _nodeContext.Info(
                $"DetectStreamDataGaps: the archive cannot count windows per day for this batch " +
                $"({ex.Message}); reading the {batch.Count} series row by row instead.");
            return null;
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(_nodeContext, _archiveRtId, ex);
        }

        var counts = new Dictionary<(OctoObjectId, DateTime), long>();
        foreach (var row in result.Rows)
        {
            var bin = row.Timestamp
                      ?? StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, StreamDataNodeHelpers.TimestampColumn) as DateTime?;
            if (row.RtId is not { } rtId || bin is null)
            {
                continue;
            }

            var value = StreamDataNodeHelpers.ResolveStreamAggregationValue(row.Values,
                StreamDataNodeHelpers.WindowStartColumn, AggregationFunction.Count);
            var count = value is null ? 0 : Convert.ToInt64(value, System.Globalization.CultureInfo.InvariantCulture);
            if (count != 0)
            {
                counts[(rtId, StreamDataNodeHelpers.ToUtc(bin.Value))] = count;
            }
        }

        return counts;
    }

    /// <summary>
    /// Reads the windows of <paramref name="rtIds" /> overlapping [from, to) row by row: only
    /// <c>window_start</c> (the end arrives as the row timestamp), in time slices sized to about one
    /// page, each paged until a short page. A window overlapping two slices comes back twice and is
    /// kept once.
    /// </summary>
    private async Task ReadWindowsAsync(IReadOnlyList<OctoObjectId> rtIds, DateTime from, DateTime to,
        Request request, Dictionary<OctoObjectId, HashSet<StreamDataGapAnalyzer.Window>> windows)
    {
        var slotsPerSlice = Math.Max(1, _pageRows / Math.Max(1, rtIds.Count));
        var rangeTicks = (to - from).Ticks;
        // Guarded against overflow: a slice longer than the range is just the range.
        var sliceTicks = request.Interval.Ticks >= rangeTicks / slotsPerSlice
            ? rangeTicks
            : request.Interval.Ticks * slotsPerSlice;
        // At least an hour (or the whole range): with a very short slot a slice sized by the slot
        // alone would turn a sparse or empty range into thousands of queries. Dense slices stay
        // correct, they just need more pages.
        sliceTicks = Math.Max(sliceTicks, Math.Min(rangeTicks, MinSliceTicks));
        var slice = TimeSpan.FromTicks(Math.Max(1, sliceTicks));

        for (var sliceFrom = from; sliceFrom < to;)
        {
            var sliceTo = to - sliceFrom > slice ? sliceFrom + slice : to;

            for (var offset = 0;;)
            {
                var options = StreamDataQueryOptions.Create()
                    .WithCkTypeId(_snapshot.TargetCkTypeId)
                    .WithColumns([StreamDataNodeHelpers.WindowStartColumn])
                    .WithRtIds(rtIds)
                    .WithTimeRange(sliceFrom, sliceTo)
                    .WithFieldFilters(request.FieldFilters)
                    // The full row key as order, so offset pages are stable on their own (the engine
                    // appends the same key since AB#9110; stating it keeps the paging independent of that).
                    .WithSortOrders([
                        new SortOrderItem(StreamDataNodeHelpers.WindowStartColumn, SortOrders.Ascending),
                        new SortOrderItem(StreamDataNodeHelpers.WindowEndColumn, SortOrders.Ascending),
                        new SortOrderItem(RtIdColumn, SortOrders.Ascending)
                    ])
                    .WithPagination(offset, _pageRows);

                StreamDataQueryResult result;
                try
                {
                    RowQueries++;
                    result = await _repository.ExecuteQueryAsync(_archiveRtId, options);
                }
                catch (Exception ex)
                {
                    throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(_nodeContext,
                        _archiveRtId, ex);
                }

                RowsRead += result.Rows.Count;
                foreach (var row in result.Rows)
                {
                    if (row.RtId is { } rtId && windows.TryGetValue(rtId, out var set)
                                             && StreamDataGapAnalyzer.TryGetWindow(row, out var window))
                    {
                        set.Add(window);
                    }
                }

                if (result.Rows.Count < _pageRows)
                {
                    break;
                }

                offset += result.Rows.Count;
            }

            sliceFrom = sliceTo;
        }
    }

    /// <summary>
    /// Whether and how the COUNT shortcut applies: whole 24-hour bins from <c>From</c> up to
    /// <see cref="CountEnd" />, each holding <see cref="SlotsPerBin" /> slots. A window count only
    /// equals "every slot delivered" when the windows are one slot long, so the archive must declare
    /// the slot length as its period; otherwise every bin is read row by row.
    /// </summary>
    private readonly record struct CountPlan(bool Usable, int Days, DateTime CountEnd, long SlotsPerBin)
    {
        internal static CountPlan Create(Request request, TimeSpan? archivePeriod)
        {
            var days = (request.To - request.From).Ticks / Bin.Ticks;
            var usable = archivePeriod == request.Interval
                         && days is >= 1 and <= MaxCountBins
                         && Bin.Ticks % request.Interval.Ticks == 0
                         && request.From.TimeOfDay.Ticks % TimeSpan.TicksPerMinute == 0;

            return usable
                ? new CountPlan(true, (int)days, request.From + Bin * days, Bin.Ticks / request.Interval.Ticks)
                : new CountPlan(false, 0, request.From, 0);
        }
    }
}
