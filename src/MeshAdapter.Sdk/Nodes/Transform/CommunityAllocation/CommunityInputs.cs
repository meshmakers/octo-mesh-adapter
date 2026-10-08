using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.CommunityAllocation;

/// <summary>
/// Reads the raw slot values of the community's input anchors from a time-range archive. Extracted
/// from <c>AllocateCommunityEnergy@1</c> unchanged; the provisional balance reads through the same
/// code, so both see the same value for the same slot.
/// </summary>
internal static class CommunityInputReader
{
    /// <param name="systemContext">Resolves the tenant's stream-data repository and archive store.</param>
    /// <param name="tenantId">The tenant.</param>
    /// <param name="archiveRtIdText">Runtime id of the archive.</param>
    /// <param name="valueColumn">Archive column with the slot quantity.</param>
    /// <param name="qualityColumn">Archive column with the data quality key.</param>
    /// <param name="anchorIds">Input anchors to read; an empty list returns an empty result without touching the archive.</param>
    /// <param name="slotStarts">Valid slot starts; a row whose window starts elsewhere is ignored.</param>
    /// <param name="from">Start of the read window (UTC).</param>
    /// <param name="to">End of the read window (UTC).</param>
    /// <param name="slotLength">A row counts only when its window lasts exactly one slot.</param>
    /// <param name="pageSize">Rows per archive page.</param>
    /// <param name="nodeContext">Receives the debug lines.</param>
    /// <param name="nodeName">Node name prefixed to every log line.</param>
    public static async Task<Dictionary<(string, DateTime), CommunityRawValue>> ReadAsync(
        ISystemContext systemContext,
        string tenantId,
        string archiveRtIdText,
        string valueColumn,
        string qualityColumn,
        IReadOnlyCollection<string> anchorIds,
        IReadOnlySet<DateTime> slotStarts,
        DateTime from,
        DateTime to,
        TimeSpan slotLength,
        int pageSize,
        INodeContext nodeContext,
        string nodeName)
    {
        var result = new Dictionary<(string, DateTime), CommunityRawValue>();
        var ids = anchorIds
            .Distinct(StringComparer.Ordinal)
            .OrderBy(x => x, StringComparer.Ordinal)
            .Select(x => new OctoObjectId(x))
            .ToList();
        if (ids.Count == 0)
        {
            return result;
        }

        if (!OctoObjectId.TryParse(archiveRtIdText, out var archiveRtId))
        {
            throw MeshAdapterPipelineExecutionException.InvalidRtId(nodeContext, archiveRtIdText);
        }

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext, tenantId);
        var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                       ?? throw MeshAdapterPipelineExecutionException.ArchiveNotFound(nodeContext, archiveRtId);

        var resolver = StreamDataNodeHelpers.CreateFieldResolver(snapshot);
        var windowStart = StreamDataNodeHelpers.ResolveQueryableColumn("WindowStart", snapshot, resolver, nodeContext, "projection");
        var windowEnd = StreamDataNodeHelpers.ResolveQueryableColumn("WindowEnd", snapshot, resolver, nodeContext, "projection");
        var value = StreamDataNodeHelpers.ResolveQueryableColumn(valueColumn, snapshot, resolver, nodeContext, "projection");
        var quality = StreamDataNodeHelpers.ResolveQueryableColumn(qualityColumn, snapshot, resolver, nodeContext, "projection");
        var rtIdColumn = StreamDataNodeHelpers.ResolveQueryableColumn("rtId", snapshot, resolver, nodeContext, "sorting");

        var ignored = 0;
        var offset = 0;
        while (true)
        {
            var options = StreamDataQueryOptions.Create()
                .WithCkTypeId(snapshot.TargetCkTypeId)
                .WithColumns([windowStart.QueryName, windowEnd.QueryName, value.QueryName, quality.QueryName])
                .WithRtIds(ids)
                .WithTimeRange(from, to)
                // A total order (window start, then rtId) keeps offset paging stable.
                .WithSortOrders([
                    new SortOrderItem(windowStart.QueryName, SortOrders.Ascending),
                    new SortOrderItem(rtIdColumn.QueryName, SortOrders.Ascending)
                ])
                .WithPagination(offset, pageSize);

            StreamDataQueryResult page;
            try
            {
                page = await repository.ExecuteQueryAsync(archiveRtId, options);
            }
            catch (Exception ex)
            {
                throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(nodeContext, archiveRtId, ex);
            }

            foreach (var row in page.Rows)
            {
                var start = CommunityValues.AsUtc(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, windowStart.StorageKey));
                var end = CommunityValues.AsUtc(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, windowEnd.StorageKey))
                          ?? CommunityValues.AsUtc(row.Timestamp);
                if (row.RtId is null || start is null || end is null || end.Value - start.Value != slotLength
                    || !slotStarts.Contains(start.Value))
                {
                    ignored++;
                    continue;
                }

                var v = CommunityValues.AsDecimal(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, value.StorageKey));
                var q = CommunityValues.QualityName(StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, quality.StorageKey));
                // Rows arrive in a total order; the first row of a window wins.
                result.TryAdd((row.RtId.Value.ToString(), start.Value), new CommunityRawValue(v, q));
            }

            offset += page.Rows.Count;
            if (page.Rows.Count < pageSize || page.Rows.Count == 0 || offset >= page.TotalCount)
            {
                break;
            }
        }

        if (ignored > 0)
        {
            nodeContext.Debug($"{nodeName}: {ignored} archive row(s) ignored because their window is not one slot of a requested day.");
        }

        nodeContext.Debug($"{nodeName}: read {offset} archive row(s) for {ids.Count} input anchor(s).");
        return result;
    }
}

/// <summary>Value conversions shared by the community nodes.</summary>
internal static class CommunityValues
{
    /// <summary>
    /// Quality keys 1, 2, 3, 4 become L1, L2, L3, Manual (Basic.Energy DataQuality); any other present
    /// value becomes L1. Manual is kept, not folded into L1, because the slot quality is the worst input
    /// quality (V-2) and a manual value must not pass as a measurement.
    /// </summary>
    internal static string QualityName(object? raw)
        => AsInt(raw) switch
        {
            2 => "L2",
            3 => "L3",
            4 => "Manual",
            _ => raw is string s && s is "L1" or "L2" or "L3" or "Manual" ? s : "L1"
        };

    internal static decimal? AsDecimal(object? raw)
    {
        switch (raw)
        {
            case null:
                return null;
            case decimal d:
                return d;
            case double d:
                return double.IsFinite(d) ? (decimal)d : null;
            case float f:
                return float.IsFinite(f) ? (decimal)f : null;
            case long l:
                return l;
            case int i:
                return i;
            case string s when decimal.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            case IConvertible convertible:
                try
                {
                    return convertible.ToDecimal(CultureInfo.InvariantCulture);
                }
                catch (Exception ex) when (ex is FormatException or InvalidCastException or OverflowException)
                {
                    return null;
                }
            default:
                return null;
        }
    }

    internal static int? AsInt(object? raw)
        => raw switch
        {
            null => null,
            int i => i,
            long l when l is >= int.MinValue and <= int.MaxValue => (int)l,
            short s => s,
            byte b => b,
            double d when double.IsFinite(d) && d % 1 == 0 && d is >= int.MinValue and <= int.MaxValue => (int)d,
            decimal m when m % 1 == 0 && m is >= int.MinValue and <= int.MaxValue => (int)m,
            string s when int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) => parsed,
            Enum e => Convert.ToInt32(e, CultureInfo.InvariantCulture),
            _ => null
        };

    /// <summary>The data source enum key; tolerates the enum value names as well.</summary>
    internal static int? AsDataSource(object? raw)
        => AsInt(raw) ?? (raw as string) switch
        {
            { } s when s.Equals("Eda", StringComparison.OrdinalIgnoreCase) => 0,
            { } s when s.Equals("Simulated", StringComparison.OrdinalIgnoreCase) => 1,
            { } s when s.Equals("SelfReported", StringComparison.OrdinalIgnoreCase) => 2,
            _ => null
        };

    internal static DateTime? AsUtc(object? raw)
        => raw switch
        {
            DateTime dt => StreamDataNodeHelpers.ToUtc(dt),
            DateTimeOffset dto => dto.UtcDateTime,
            long ms => DateTime.UnixEpoch.AddMilliseconds(ms),
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => null
        };

    /// <summary>
    /// A time of day from the data context (<c>HH:mm</c> or <c>HH:mm:ss</c>); null when the path is
    /// not set or resolves to nothing or an empty string.
    /// </summary>
    internal static TimeSpan? ResolveTimeOfDay(IDataContext dataContext, INodeContext nodeContext, string? path,
        string nodeName)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = dataContext.GetValue(path);
        return value switch
        {
            null => null,
            TimeSpan ts => ts,
            string s when string.IsNullOrWhiteSpace(s) => null,
            string s when TimeSpan.TryParseExact(s, ["hh\\:mm", "hh\\:mm\\:ss"], CultureInfo.InvariantCulture,
                out var parsed) => parsed,
            _ => throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {nodeName}: '{path}' is not a time of day (HH:mm or HH:mm:ss).")
        };
    }

    /// <summary>A JSON number as decimal, read from its raw token; null when it does not fit.</summary>
    internal static decimal? ParseJsonNumber(System.Text.Json.Nodes.JsonValue value)
        => decimal.TryParse(value.ToJsonString(), NumberStyles.Float, CultureInfo.InvariantCulture, out var d)
            ? d
            : null;

    /// <summary>Resolves a time zone id or fails the node with a clear message.</summary>
    internal static TimeZoneInfo ResolveTimeZone(string timeZoneId, INodeContext nodeContext, string nodeName)
    {
        try
        {
            return TimeZoneInfo.FindSystemTimeZoneById(timeZoneId);
        }
        catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
        {
            throw new PipelineNodeExecutionException(
                $"[{nodeContext.NodePath}]: {nodeName}: unknown time zone '{timeZoneId}'.", ex);
        }
    }
}
