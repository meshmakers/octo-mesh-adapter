using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Ingests whole series of windowed measurements into a <c>TimeRangeArchive</c>: one anchor entity
/// per series plus one archive row per value, in a single node.
/// </summary>
/// <remarks>
/// See <see cref="SaveTimeRangeSeriesInArchiveNodeConfiguration" /> for why this exists. The short
/// version: the node composition it replaces materialises one runtime entity per measured window in
/// order to write one time-series row, which costs roughly two dozen node executions per 15-minute
/// slot. The runtime model only needs one anchor per series.
/// <para>
/// The shaping rules — anchor key, which value the anchor reflects, which values are unusable — live
/// in <see cref="TimeRangeSeriesShaper" /> so they can be tested without a CK model or a database.
/// What stays here is everything that needs one: resolving the anchors against the runtime store,
/// building their attributes through the CK model, and the archive write.
/// </para>
/// </remarks>
[NodeConfiguration(typeof(SaveTimeRangeSeriesInArchiveNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SaveTimeRangeSeriesInArchiveNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISystemContext systemContext,
    ICkCacheService ckCacheService)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SaveTimeRangeSeriesInArchiveNodeConfiguration>();
        ValidateConfiguration(c);

        var seriesArray = dataContext.Get<JsonArray>(c.Path);
        if (seriesArray == null || seriesArray.Count == 0)
        {
            nodeContext.Warning($"No series found at '{c.Path}'");
            await next(dataContext, nodeContext);
            return;
        }

        var series = TimeRangeSeriesShaper.Shape(seriesArray, c, out var unresolvedKeys);
        if (unresolvedKeys > 0)
        {
            nodeContext.Warning(
                $"Skipped {unresolvedKeys} series whose well-known name '{c.WellKnownNameFormat}' did not resolve.");
        }

        if (series.Count == 0)
        {
            nodeContext.Warning("No series carried both a well-known name and at least one value");
            await next(dataContext, nodeContext);
            return;
        }

        var ckTypeId = new RtCkId<CkTypeId>(c.CkTypeId);
        var valueCount = series.Sum(s => s.Values.Count);

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SaveTimeRangeSeriesInArchive, new
            {
                archiveRtId = c.ArchiveRtId,
                path = c.Path,
                ckTypeId = c.CkTypeId,
                seriesCount = series.Count,
                valueCount,
                wouldWriteAnchors = series.Select(s => s.WellKnownName).ToList()
            });
            await next(dataContext, nodeContext);
            return;
        }

        await ResolveAndPersistAnchorsAsync(series, ckTypeId, c, nodeContext);
        await WriteArchiveRowsAsync(series, ckTypeId, c, nodeContext);

        await next(dataContext, nodeContext);
    }

    private static void ValidateConfiguration(SaveTimeRangeSeriesInArchiveNodeConfiguration c)
    {
        if (string.IsNullOrWhiteSpace(c.ArchiveRtId))
        {
            throw new InvalidOperationException(
                "SaveTimeRangeSeriesInArchive: archiveRtId is required.");
        }

        if (c.Columns.Count == 0)
        {
            throw new InvalidOperationException(
                "SaveTimeRangeSeriesInArchive: at least one column is required — a write with no " +
                "columns would store nothing but window boundaries.");
        }

        // All three parent settings or none: a half-configured association silently produces anchors
        // with no parent, which only surfaces much later as an empty navigation.
        var configured = new[] { c.ParentRtIdProperty, c.ParentCkTypeId, c.ParentAssociationRoleId }
            .Count(p => !string.IsNullOrWhiteSpace(p));
        if (configured is not 0 and not 3)
        {
            throw new InvalidOperationException(
                "SaveTimeRangeSeriesInArchive: parentRtIdProperty, parentCkTypeId and " +
                "parentAssociationRoleId must be configured together or not at all.");
        }
    }

    /// <summary>
    /// Looks every anchor up by well-known name in one query, assigns each one its canonical RtId,
    /// and persists the new and advanced anchors plus their parent associations.
    /// </summary>
    /// <remarks>
    /// This runs before the archive write on purpose: the archive's orphan guard rejects rows whose
    /// source entity does not exist, so the anchors have to be committed first.
    /// </remarks>
    private async Task ResolveAndPersistAnchorsAsync(
        IReadOnlyList<ShapedSeries> series,
        RtCkId<CkTypeId> ckTypeId,
        SaveTimeRangeSeriesInArchiveNodeConfiguration c,
        INodeContext nodeContext)
    {
        var wellKnownNames = series.Select(s => s.WellKnownName).ToList();

        // AB#5028 / AB#5127 — scoped (config-selected, default Caller): this reads and writes the
        // tenant's own measurement entities, so it must be stamped and subject to data permissions
        // like any other business-data node. A narrower identity that hides an existing anchor makes
        // the node insert a duplicate rather than fail, which is the right trade here: the
        // alternative (System) would write unstamped entities nobody can scope afterwards.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();
        var existing = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session,
            ckTypeId,
            RtEntityQueryOptions.Create().FieldIn(nameof(RtEntity.RtWellKnownName), wellKnownNames),
            0,
            wellKnownNames.Count);
        await session.CommitTransactionAsync();

        var existingByName = existing.Items
            .Where(e => !string.IsNullOrEmpty(e.RtWellKnownName))
            .ToDictionary(e => e.RtWellKnownName!, StringComparer.Ordinal);

        var entityUpdates = new List<EntityUpdateInfo<RtEntity>>();
        var associationUpdates = new List<AssociationUpdateInfo>();

        foreach (var shaped in series)
        {
            var winner = TimeRangeSeriesShaper.SelectAnchorValue(shaped.Values, c);

            if (existingByName.TryGetValue(shaped.WellKnownName, out var stored))
            {
                shaped.RtId = stored.RtId;

                if (!ShouldAdvanceAnchor(stored, winner, c))
                {
                    // The stored anchor already reflects a newer window. Its archive rows are still
                    // written below — only the runtime snapshot stays where it is.
                    continue;
                }

                var updated = BuildAnchorEntity(ckTypeId, shaped.WellKnownName, shaped.Series, winner, c);
                updated.RtId = stored.RtId;
                entityUpdates.Add(EntityUpdateInfo<RtEntity>.CreateUpdate(
                    new RtEntityId(ckTypeId, stored.RtId), updated));
                continue;
            }

            shaped.RtId = OctoObjectId.GenerateNewId();

            var inserted = BuildAnchorEntity(ckTypeId, shaped.WellKnownName, shaped.Series, winner, c);
            inserted.RtId = shaped.RtId;
            entityUpdates.Add(EntityUpdateInfo<RtEntity>.CreateInsert(inserted));

            var association = BuildParentAssociation(shaped, ckTypeId, c, nodeContext);
            if (association is not null)
            {
                associationUpdates.Add(association);
            }
        }

        if (entityUpdates.Count == 0 && associationUpdates.Count == 0)
        {
            return;
        }

        // AB#5028 / AB#5127 — scoped, same identity as the lookup above: the anchors are tenant
        // business data and carry a creator stamp. A separate session because the lookup's
        // transaction is already committed.
        var writeSession = await etlContext.GetSessionForAsync(c.Identity);
        writeSession.StartTransaction();
        var operationResult = new OperationResult();
        await etlContext.TenantRepository.ApplyChangesAsync(
            writeSession, entityUpdates, associationUpdates, operationResult);

        if (operationResult.HasErrors || operationResult.HasFatalErrors)
        {
            await writeSession.AbortTransactionAsync();
            // Aborting leaves the freshly generated RtIds uncommitted, so the archive write that
            // follows would produce orphan rows. Fail the node instead.
            throw new InvalidOperationException(
                $"SaveTimeRangeSeriesInArchive: persisting {entityUpdates.Count} anchor entity/entities " +
                $"failed ({operationResult.GetMessages()}); refusing to write archive rows that would " +
                "reference them.");
        }

        await writeSession.CommitTransactionAsync();
        nodeContext.Debug(
            $"Persisted {entityUpdates.Count} anchor entity/entities and {associationUpdates.Count} association(s)");
    }

    private bool ShouldAdvanceAnchor(
        RtEntity stored, JsonObject winner, SaveTimeRangeSeriesInArchiveNodeConfiguration c)
    {
        if (c.AnchorWindowToAttribute is null)
        {
            return true;
        }

        var winnerEnd = TimeRangeSeriesShaper.ReadDateTime(winner, c.ToProperty);
        if (winnerEnd is null)
        {
            return true;
        }

        var storedEnd = RtPathEvaluator.GetValue(
            ckCacheService, etlContext.TenantId, stored, c.AnchorWindowToAttribute) switch
        {
            DateTime dt => TimeRangeSeriesShaper.NormaliseToUtc(dt),
            DateTimeOffset dto => dto.UtcDateTime,
            _ => (DateTime?)null
        };

        return storedEnd is null || winnerEnd.Value > storedEnd.Value;
    }

    private RtEntity BuildAnchorEntity(
        RtCkId<CkTypeId> ckTypeId,
        string wellKnownName,
        JsonObject series,
        JsonObject value,
        SaveTimeRangeSeriesInArchiveNodeConfiguration c)
    {
        var entity = new RtEntity
        {
            CkTypeId = ckTypeId,
            RtWellKnownName = wellKnownName,
            RtChangedDateTime = DateTime.UtcNow
        };

        foreach (var column in c.Columns)
        {
            var source = column.Scope == TimeRangeSeriesColumnScope.Series ? series : value;
            var scalar = TimeRangeSeriesShaper.ToScalar(source[column.ValueProperty]);
            if (scalar is not null)
            {
                // Through the CK model rather than straight into the attribute dictionary: an
                // attribute path like "Amount.Value" has to materialise the Amount record, which
                // only the model knows the shape of.
                RtPathEvaluator.SetValue(ckCacheService, etlContext.TenantId, entity, column.Name, scalar);
            }
        }

        if (c.AnchorWindowFromAttribute is not null
            && TimeRangeSeriesShaper.ReadDateTime(value, c.FromProperty) is { } from)
        {
            RtPathEvaluator.SetValue(ckCacheService, etlContext.TenantId, entity,
                c.AnchorWindowFromAttribute, from);
        }

        if (c.AnchorWindowToAttribute is not null
            && TimeRangeSeriesShaper.ReadDateTime(value, c.ToProperty) is { } to)
        {
            RtPathEvaluator.SetValue(ckCacheService, etlContext.TenantId, entity,
                c.AnchorWindowToAttribute, to);
        }

        return entity;
    }

    private static AssociationUpdateInfo? BuildParentAssociation(
        ShapedSeries shaped,
        RtCkId<CkTypeId> ckTypeId,
        SaveTimeRangeSeriesInArchiveNodeConfiguration c,
        INodeContext nodeContext)
    {
        if (c.ParentRtIdProperty is null)
        {
            return null;
        }

        var text = TimeRangeSeriesShaper
            .ToScalar(shaped.Series[c.ParentRtIdProperty], parseDateStrings: false)?.ToString();
        if (string.IsNullOrWhiteSpace(text) || !OctoObjectId.TryParse(text, out var parentRtId))
        {
            nodeContext.Warning(
                $"Series '{shaped.WellKnownName}' has no usable parent RtId at '{c.ParentRtIdProperty}'; " +
                "the anchor is created without a parent association.");
            return null;
        }

        return AssociationUpdateInfo.CreateInsert(
            new RtEntityId(ckTypeId, shaped.RtId),
            new RtEntityId(new RtCkId<CkTypeId>(c.ParentCkTypeId!), parentRtId),
            new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId!));
    }

    /// <summary>
    /// Writes every value of every series in a single bulk insert. Ordering between competing writes
    /// for the same window is the archive's business, not this node's — see the archive's
    /// <c>ConflictVersionColumn</c>.
    /// </summary>
    private async Task WriteArchiveRowsAsync(
        IReadOnlyList<ShapedSeries> series,
        RtCkId<CkTypeId> ckTypeId,
        SaveTimeRangeSeriesInArchiveNodeConfiguration c,
        INodeContext nodeContext)
    {
        var points = TimeRangeSeriesShaper.BuildRows(series, ckTypeId, c, out var skippedNoWindow);

        if (skippedNoWindow > 0)
        {
            nodeContext.Debug(
                $"Skipped {skippedNoWindow} value(s) without a usable [{c.FromProperty}, {c.ToProperty}) window.");
        }

        if (points.Count == 0)
        {
            nodeContext.Warning("No archive rows to write");
            return;
        }

        var tenantContext = await systemContext.FindTenantContextAsync(etlContext.TenantId);
        var streamDataRepo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException(
                $"Stream data repository is not available for tenant '{etlContext.TenantId}'. " +
                "Ensure AddCrateDbStreamDataRepository() was called during startup.");

        await streamDataRepo.EnsureDatabaseCreatedAsync();

        nodeContext.Debug(
            $"Inserting {points.Count} time-range data point(s) for {series.Count} series into archive '{c.ArchiveRtId}'");
        await streamDataRepo.InsertTimeRangeAsync(new OctoObjectId(c.ArchiveRtId), points);
    }
}
