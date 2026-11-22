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
    internal readonly record struct Request(
        DateTime From,
        DateTime To,
        TimeSpan Interval,
        IReadOnlyList<OctoObjectId> RtIds,
        IReadOnlyList<FieldFilter>? FieldFilters);

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

        var plan = CountPlan.Create(request, _snapshot.Period);
        var batchSize = plan.Usable
            ? Math.Max(1, Math.Min(_maxSeriesPerBatch, MaxCountRowsPerQuery / plan.Days))
            : _maxSeriesPerBatch;

        var withGaps = new List<StreamDataGapDetectionSeries>();
        var overlapping = new List<string>();
        long missingTotal = 0;

        for (var offset = 0; offset < series.Count; offset += batchSize)
        {
            var batch = series.GetRange(offset, Math.Min(batchSize, series.Count - offset));
            var windows = batch.ToDictionary(id => id, _ => new HashSet<StreamDataGapAnalyzer.Window>());

            await CollectWindowsAsync(batch, windows, request, plan);

            foreach (var rtId in batch)
            {
                var analysed = StreamDataGapAnalyzer.AnalyseSeries(
                    new StreamDataGapAnalyzer.Series { RtId = rtId, Windows = windows[rtId].ToList() },
                    request.From, request.To, request.Interval);

                if (analysed.HasOverlaps)
                {
                    overlapping.Add(rtId.ToString());
                }

                if (analysed.Gaps.Count == 0)
                {
                    continue;
                }

                var missing = analysed.MissingIntervals ?? 0;
                missingTotal += missing;
                withGaps.Add(new StreamDataGapDetectionSeries
                {
                    RtId = rtId.ToString(),
                    MissingCount = missing,
                    HasOverlaps = analysed.HasOverlaps,
                    Ranges = analysed.Gaps
                        .Select(g => new StreamDataGapDetectionRange
                        {
                            Start = g.From,
                            End = g.To,
                            MissingCount = g.MissingIntervals ?? 0
                        })
                        .ToList()
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
            ExpectedCount = (int)Math.Ceiling((double)(request.To - request.From).Ticks / request.Interval.Ticks),
            SeriesCount = series.Count,
            SeriesWithGapsCount = withGaps.Count,
            MissingCount = missingTotal,
            Series = withGaps
        };
    }

    private async Task CollectWindowsAsync(IReadOnlyList<OctoObjectId> batch,
        Dictionary<OctoObjectId, HashSet<StreamDataGapAnalyzer.Window>> windows, Request request, CountPlan plan)
    {
        var rowReadFrom = request.From;

        if (plan.Usable && await TryCountAsync(batch, request, plan) is { } counts)
        {
            var partialBins = new SortedDictionary<DateTime, List<OctoObjectId>>();

            foreach (var rtId in batch)
            {
                for (var day = 0; day < plan.Days; day++)
                {
                    var binStart = request.From + Bin * day;
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

        if (rowReadFrom < request.To)
        {
            await ReadWindowsAsync(batch, rowReadFrom, request.To, request, windows);
        }
    }

    /// <summary>
    /// Windows per series and bin, or null when the storage refused the bins (the caller then reads
    /// the batch row by row). Any other failure fails the node with the storage message.
    /// </summary>
    private async Task<Dictionary<(OctoObjectId RtId, DateTime Bin), long>?> TryCountAsync(
        IReadOnlyList<OctoObjectId> batch, Request request, CountPlan plan)
    {
        var options = StreamDataDownsamplingQueryOptions.Create()
            .WithCkTypeId(_snapshot.TargetCkTypeId)
            .WithAggregationColumns([new AggregationColumn(StreamDataNodeHelpers.WindowStartColumn, AggregationFunction.Count)])
            .WithGroupByColumns([RtIdColumn])
            .WithRtIds(batch)
            .WithFieldFilters(request.FieldFilters)
            .WithTimeRange(request.From, plan.CountEnd)
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
                    .WithSortOrders([new SortOrderItem(StreamDataNodeHelpers.WindowStartColumn, SortOrders.Ascending)])
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
