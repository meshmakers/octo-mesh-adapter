using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Condenses archive columns into key figures over a time range — the sum of a month's energy, the
/// maximum data quality in that month — optionally grouped, e.g. per source entity. Sibling of
/// <c>GetStreamData@1</c>, which returns the rows themselves; sorting, paging and a row cap have no
/// meaning here and are absent from the configuration.
/// <para>
/// With <c>RequireGapFree</c> the range's coverage is checked first, so an incomplete month cannot
/// silently produce a figure that looks valid but is too low.
/// </para>
/// </summary>
/// <param name="next">Next node delegate in the pipeline</param>
/// <param name="context">Mesh ETL context</param>
/// <param name="systemContext">System context used to resolve the tenant-scoped stream-data repository</param>
[NodeConfiguration(typeof(AggregateStreamDataNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class AggregateStreamDataNode(
    NodeDelegate next,
    IMeshEtlContext context,
    ISystemContext systemContext)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<AggregateStreamDataNodeConfiguration>();

        var (streamDataRepo, snapshot) = await ResolveArchiveAsync(c.ArchiveRtId, nodeContext);
        var resolver = StreamDataNodeHelpers.CreateFieldResolver(snapshot);

        // Literal configuration wins over the JSONPath variant; both boundaries are normalised to UTC
        // so the adapter's local zone can never shift the aggregated window (AB#4734).
        var from = StreamDataNodeHelpers.ToUtcOrNull(c.From)
                   ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.FromPath,
                       nameof(c.FromPath), "the start of the time range stays open.");
        var to = StreamDataNodeHelpers.ToUtcOrNull(c.To)
                 ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.ToPath,
                     nameof(c.ToPath), "the end of the time range stays open.");

        if (from.HasValue && to.HasValue && from >= to)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataTimeRangeInvalid(nodeContext, from, to);
        }

        var aggregations = ResolveAggregations(c, snapshot, resolver, nodeContext);
        var groupBy = ResolveGroupBy(c, snapshot, resolver, nodeContext);

        if (c.RequireGapFree)
        {
            ValidateGapGuardConfiguration(c, snapshot, from, to, nodeContext);
        }

        var rtIds = StreamDataNodeHelpers.ResolveRtIds(c.RtIds, c.RtIdsPath, dataContext, nodeContext,
            nameof(c.RtIdsPath));
        var fieldFilters = StreamDataNodeHelpers.BuildFieldFilters(c.WellKnownNames,
            c.WellKnownNamesPath, c.FieldFilters, snapshot, resolver, dataContext, nodeContext,
            nameof(c.WellKnownNamesPath));

        if (c.RequireGapFree)
        {
            // Checked before the aggregation, so an incomplete range never produces a figure at all.
            var report = await StreamDataGapScanner.ScanAsync(streamDataRepo, snapshot, c.ArchiveRtId,
                new StreamDataGapScanner.Request(from!.Value, to!.Value, c.ExpectedInterval,
                    c.MaxGapScanRows, rtIds, fieldFilters),
                nodeContext);

            if (!report.IsComplete)
            {
                throw MeshAdapterPipelineExecutionException.AggregationGapGuardFailed(nodeContext,
                    c.ArchiveRtId, DescribeIncompleteSeries(report));
            }
        }

        var result = await ExecuteAggregationAsync(streamDataRepo, snapshot, c, aggregations, groupBy,
            rtIds, fieldFilters, from, to, nodeContext);

        var queryResult = BuildQueryResult(aggregations, groupBy, result);

        dataContext.Set(c.TargetPath, queryResult, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// One requested key figure: the header the result keeps (the configured attribute path), the
    /// physical column to aggregate, and the enum the value lookup needs.
    /// </summary>
    private readonly record struct Aggregation(
        string Header, string QueryName, string StorageKey, AggregationTypesDto Function);

    /// <summary>
    /// Resolves the tenant's stream-data repository and the archive's snapshot. The snapshot supplies
    /// the CkTypeId every query option requires, plus the columns and storage shape the validation
    /// below checks against.
    /// </summary>
    private async Task<(IStreamDataRepository Repository, ArchiveSnapshot Snapshot)> ResolveArchiveAsync(
        OctoObjectId archiveRtId, INodeContext nodeContext)
    {
        var tenantId = context.TenantId;
        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);

        var repository = tenantContext.GetStreamDataRepository()
                         ?? throw MeshAdapterPipelineExecutionException.StreamDataNotEnabled(nodeContext,
                             tenantId);

        var snapshot = await tenantContext.GetArchiveRuntimeStore().GetAsync(archiveRtId)
                       ?? throw MeshAdapterPipelineExecutionException.ArchiveNotFound(nodeContext,
                           archiveRtId);

        return (repository, snapshot);
    }

    /// <summary>
    /// Validates the requested aggregations and translates their column names. The header keeps the
    /// configured attribute path so the caller recognises its own request; where the same path is
    /// aggregated more than once the function is appended, because the result keys are unique per
    /// function but a bare path header would not be.
    /// </summary>
    private static List<Aggregation> ResolveAggregations(AggregateStreamDataNodeConfiguration c,
        ArchiveSnapshot snapshot, StreamDataFieldResolver resolver, INodeContext nodeContext)
    {
        var configured = c.Aggregations?
            .Where(a => a is not null && !string.IsNullOrWhiteSpace(a.AttributePath))
            .ToList() ?? [];

        if (configured.Count == 0)
        {
            throw MeshAdapterPipelineExecutionException.AggregationColumnsMissing(nodeContext);
        }

        var duplicatePaths = configured
            .GroupBy(a => a.AttributePath, StringComparer.OrdinalIgnoreCase)
            .Where(g => g.Count() > 1)
            .Select(g => g.Key)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var result = new List<Aggregation>(configured.Count);
        foreach (var aggregation in configured)
        {
            EnsureSupportedFunction(aggregation.Function, aggregation.AttributePath, nodeContext);

            var header = duplicatePaths.Contains(aggregation.AttributePath)
                ? $"{aggregation.AttributePath} ({aggregation.Function})"
                : aggregation.AttributePath;

            var resolved = StreamDataNodeHelpers.ResolveQueryableColumn(aggregation.AttributePath,
                snapshot, resolver, nodeContext, "aggregating");

            result.Add(new Aggregation(header, resolved.QueryName, resolved.StorageKey,
                aggregation.Function));
        }

        return result;
    }

    /// <summary>
    /// Only the five functions the shared mapping knows are accepted. A time-weighted average and a
    /// state duration need metadata this node cannot carry — a comparison value, or the raw archive's
    /// LOCF path — and their result keys follow different rules, so they are refused with the route
    /// that does support them.
    /// </summary>
    private static void EnsureSupportedFunction(AggregationTypesDto function, string attributePath,
        INodeContext nodeContext)
    {
        switch (function)
        {
            case AggregationTypesDto.Count:
            case AggregationTypesDto.Sum:
            case AggregationTypesDto.Average:
            case AggregationTypesDto.Minimum:
            case AggregationTypesDto.Maximum:
                return;
            default:
                throw MeshAdapterPipelineExecutionException.UnsupportedAggregationFunction(nodeContext,
                    function.ToString(), attributePath);
        }
    }

    /// <summary>
    /// Translates the group-by column names the same way the filters are translated, so a mistyped
    /// one fails instead of being dropped — a dropped group-by column would collapse every group into
    /// one row and quietly change what the figures mean.
    /// </summary>
    private static List<StreamDataNodeHelpers.ResolvedColumn> ResolveGroupBy(
        AggregateStreamDataNodeConfiguration c, ArchiveSnapshot snapshot,
        StreamDataFieldResolver resolver, INodeContext nodeContext)
    {
        return c.GroupBy?
            .Where(col => !string.IsNullOrWhiteSpace(col))
            .Select(col => StreamDataNodeHelpers.ResolveQueryableColumn(col, snapshot, resolver,
                nodeContext, "grouping"))
            .ToList() ?? [];
    }

    private static void ValidateGapGuardConfiguration(AggregateStreamDataNodeConfiguration c,
        ArchiveSnapshot snapshot, DateTime? from, DateTime? to, INodeContext nodeContext)
    {
        if (from is null || to is null)
        {
            throw MeshAdapterPipelineExecutionException.GapDetectionTimeRangeRequired(nodeContext);
        }

        if (!snapshot.UsesWindowedStorage)
        {
            throw MeshAdapterPipelineExecutionException.GapDetectionRequiresWindowedArchive(nodeContext,
                c.ArchiveRtId);
        }

        // Rejected rather than quietly substituted, same as on GetStreamData@1: a configured value
        // that means nothing is a mistake worth naming.
        if (c.MaxGapScanRows is <= 0)
        {
            throw MeshAdapterPipelineExecutionException.GapScanRowLimitInvalid(nodeContext,
                c.MaxGapScanRows);
        }

        if (c.ExpectedInterval is { Ticks: <= 0 })
        {
            throw MeshAdapterPipelineExecutionException.ExpectedIntervalInvalid(nodeContext,
                c.ExpectedInterval.Value);
        }
    }

    /// <summary>
    /// Grouped and ungrouped aggregation are separate storage calls returning different row counts:
    /// one row per group versus a single row overall.
    /// </summary>
    private static async Task<StreamDataQueryResult> ExecuteAggregationAsync(
        IStreamDataRepository streamDataRepo, ArchiveSnapshot snapshot,
        AggregateStreamDataNodeConfiguration c, List<Aggregation> aggregations,
        List<StreamDataNodeHelpers.ResolvedColumn> groupBy, IReadOnlyList<OctoObjectId>? rtIds, IReadOnlyList<FieldFilter>? fieldFilters, DateTime? from,
        DateTime? to, INodeContext nodeContext)
    {
        var columns = aggregations
            .Select(a => new AggregationColumn(a.QueryName,
                StreamDataNodeHelpers.MapStreamAggregation(a.Function).Function))
            .ToList();

        try
        {
            if (groupBy.Count > 0)
            {
                var grouped = StreamDataGroupedAggregationQueryOptions.Create()
                    .WithCkTypeId(snapshot.TargetCkTypeId)
                    .WithGroupByColumns(groupBy.Select(g => g.QueryName).ToList())
                    .WithAggregationColumns(columns)
                    .WithRtIds(rtIds)
                    .WithTimeRange(from, to)
                    .WithFieldFilters(fieldFilters);

                return await streamDataRepo.ExecuteGroupedAggregationQueryAsync(c.ArchiveRtId, grouped);
            }

            var options = StreamDataAggregationQueryOptions.Create()
                .WithCkTypeId(snapshot.TargetCkTypeId)
                .WithAggregationColumns(columns)
                .WithRtIds(rtIds)
                .WithTimeRange(from, to)
                .WithFieldFilters(fieldFilters);

            return await streamDataRepo.ExecuteAggregationQueryAsync(c.ArchiveRtId, options);
        }
        catch (Exception ex)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataArchiveQueryFailed(nodeContext,
                c.ArchiveRtId, ex);
        }
    }

    /// <summary>
    /// Maps the aggregates into the tabular result shape the pipeline works with, with parity to the
    /// persisted-query path: group-by columns first, then one column per key figure. Without grouping
    /// there is exactly one row.
    /// </summary>
    private static QueryResult BuildQueryResult(List<Aggregation> aggregations,
        List<StreamDataNodeHelpers.ResolvedColumn> groupBy, StreamDataQueryResult result)
    {
        var queryResult = new QueryResult();

        queryResult.Columns.AddRange(groupBy.Select(col =>
            new QueryResultColumns { Header = col.QueryName }));
        queryResult.Columns.AddRange(aggregations.Select(a =>
            new QueryResultColumns { Header = a.Header }));

        if (groupBy.Count == 0)
        {
            // A single row even when the storage layer returned none, so a downstream consumer always
            // finds the shape it expects; the values are then null rather than absent.
            var row = result.Rows.FirstOrDefault();
            queryResult.Rows.Add(new QueryResultRow
            {
                Values = aggregations
                    .Select(a => row is null ? null : ResolveValue(row, a))
                    .ToList()
            });

            return queryResult;
        }

        foreach (var row in result.Rows)
        {
            var values = new List<object?>();
            values.AddRange(groupBy.Select(col =>
                StreamDataNodeHelpers.ResolveStreamColumnValue(row.Values, col.StorageKey)));
            values.AddRange(aggregations.Select(a => ResolveValue(row, a)));

            queryResult.Rows.Add(new QueryResultRow
            {
                RtId = row.RtId,
                CkTypeId = row.CkTypeId,
                Values = values
            });
        }

        return queryResult;
    }

    private static object? ResolveValue(StreamDataRow row, Aggregation aggregation)
        => StreamDataNodeHelpers.ResolveStreamAggregationValue(row.Values, aggregation.StorageKey,
            aggregation.Function);

    /// <summary>
    /// Names the incomplete series for the guard's error message: the well-known name (or rtId),
    /// how much is missing and where the first hole starts — enough to act on without re-running the
    /// query with a gap report.
    /// </summary>
    private static string DescribeIncompleteSeries(StreamDataGapReport report)
    {
        var descriptions = report.Series
            .Where(s => !s.IsComplete)
            .Select(s =>
            {
                var name = s.WellKnownName ?? s.RtId?.ToString() ?? "(unknown entity)";
                var missing = s.MissingIntervals.HasValue
                    ? $"{s.MissingIntervals} interval(s)"
                    : $"{s.MissingDuration}";
                var firstGap = s.Gaps.Count > 0 ? $", first at {s.Gaps[0].From:O}" : string.Empty;
                return $"{name}: {missing} missing{firstGap}";
            });

        return string.Join("; ", descriptions);
    }
}
