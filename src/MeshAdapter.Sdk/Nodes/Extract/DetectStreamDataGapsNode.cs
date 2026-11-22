using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Finds the missing time ranges and missing slot counts of many series in one windowed stream data
/// archive over [From, To), without a row limit. Reads only the configured archive (no rollup
/// selection, no fallback to another archive); the detection itself is in
/// <see cref="StreamDataGapDetector" />.
/// </summary>
/// <param name="next">Next node delegate in the pipeline</param>
/// <param name="context">Mesh ETL context</param>
/// <param name="systemContext">System context used to resolve the tenant-scoped stream-data repository</param>
[NodeConfiguration(typeof(DetectStreamDataGapsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DetectStreamDataGapsNode(
    NodeDelegate next,
    IMeshEtlContext context,
    ISystemContext systemContext)
    : IPipelineNode
{
    /// <summary>Most slots a period may hold, so every per-series count fits into an int.</summary>
    internal const long MaxSlotsPerPeriod = int.MaxValue / 2;

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<DetectStreamDataGapsNodeConfiguration>();

        // Checked before anything is read, so a misconfiguration names its option.
        if (c.RtIds is not { Count: > 0 } && string.IsNullOrWhiteSpace(c.RtIdsPath))
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsSeriesRequired(nodeContext);
        }

        if (c.ExpectedInterval is { Ticks: <= 0 })
        {
            throw MeshAdapterPipelineExecutionException.ExpectedIntervalInvalid(nodeContext,
                c.ExpectedInterval.Value);
        }

        // An offset on the input decides the instant; values without one are read as UTC (AB#4734).
        var from = StreamDataNodeHelpers.ToUtcOrNull(c.From)
                   ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.FromPath,
                       nameof(c.FromPath), "the period has no start.")
                   ?? throw MeshAdapterPipelineExecutionException.DetectGapsBoundaryRequired(nodeContext,
                       nameof(c.From), nameof(c.FromPath));
        var to = StreamDataNodeHelpers.ToUtcOrNull(c.To)
                 ?? StreamDataNodeHelpers.ResolveDateTimeFromPath(dataContext, nodeContext, c.ToPath,
                     nameof(c.ToPath), "the period has no end.")
                 ?? throw MeshAdapterPipelineExecutionException.DetectGapsBoundaryRequired(nodeContext,
                     nameof(c.To), nameof(c.ToPath));

        if (from >= to)
        {
            throw MeshAdapterPipelineExecutionException.StreamDataTimeRangeInvalid(nodeContext, from, to);
        }

        var (repository, snapshot) = await ResolveArchiveAsync(c.ArchiveRtId, nodeContext);

        if (!snapshot.UsesWindowedStorage)
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsRequiresWindowedArchive(nodeContext,
                c.ArchiveRtId);
        }

        var interval = c.ExpectedInterval ?? snapshot.Period;
        if (interval is not { Ticks: > 0 })
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsIntervalUnknown(nodeContext, c.ArchiveRtId);
        }

        // The slot counts are ints (shared with GetStreamData@1's report). Each missing range rounds
        // up by less than one slot and there are never more ranges than slots, so a series' sum stays
        // below twice the period's slots; a period that could exceed int is refused up front.
        if ((to - from).Ticks / interval.Value.Ticks >= MaxSlotsPerPeriod)
        {
            throw MeshAdapterPipelineExecutionException.DetectGapsTooManySlots(nodeContext, interval.Value,
                MaxSlotsPerPeriod);
        }

        var rtIds = StreamDataNodeHelpers.ResolveRtIds(c.RtIds, c.RtIdsPath, dataContext, nodeContext,
            nameof(c.RtIdsPath)) ?? [];
        var fieldFilters = StreamDataNodeHelpers.BuildFieldFilters(null, null, c.FieldFilters, snapshot,
            StreamDataNodeHelpers.CreateFieldResolver(snapshot), dataContext, nodeContext,
            nameof(c.FieldFilters));

        var detector = new StreamDataGapDetector(repository, snapshot, c.ArchiveRtId, nodeContext);
        var result = await detector.DetectAsync(
            new StreamDataGapDetector.Request(from, to, interval.Value, rtIds, fieldFilters));

        dataContext.Set(c.TargetPath, result, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

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
}
