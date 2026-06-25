using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Pipeline-load node that writes externally pre-aggregated time-range data into a
/// <c>TimeRangeArchive</c> via <see cref="IStreamDataRepository.InsertTimeRangeAsync"/>.
/// Sibling of <see cref="SaveStreamDataInArchiveNode"/> for the time-range storage shape.
/// </summary>
[NodeConfiguration(typeof(SaveTimeRangeStreamDataInArchiveNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SaveTimeRangeStreamDataInArchiveNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ISystemContext systemContext)
    : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SaveTimeRangeStreamDataInArchiveNodeConfiguration>();

        if (string.IsNullOrWhiteSpace(c.ArchiveRtId))
        {
            throw new InvalidOperationException(
                "SaveTimeRangeStreamDataInArchive: archiveRtId is required. Configure the node with the runtime id of the target TimeRangeArchive.");
        }

        var archiveRtId = new OctoObjectId(c.ArchiveRtId);

        var data = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(c.Path);

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.SaveTimeRangeStreamDataInArchive, new
            {
                archiveRtId = c.ArchiveRtId,
                path = c.Path,
                fromAttributePath = c.FromAttributePath,
                toAttributePath = c.ToAttributePath,
                count = data?.Count ?? 0,
                wouldInsert = data
            });
            await next(dataContext, nodeContext);
            return;
        }

        if (data == null || data.Count == 0)
        {
            nodeContext.Warning("No update infos found");
            await next(dataContext, nodeContext);
            return;
        }

        var tenantId = etlContext.TenantId;

        var tenantContext = await systemContext.FindTenantContextAsync(tenantId);
        var streamDataRepo = tenantContext.GetStreamDataRepository()
            ?? throw new InvalidOperationException(
                $"Stream data repository is not available for tenant '{tenantId}'. " +
                "Ensure AddCrateDbStreamDataRepository() was called during startup.");

        await streamDataRepo.EnsureDatabaseCreatedAsync();

        var toInsert = new List<TimeRangeStreamDataPoint>();
        var skippedNoWindow = 0;

        foreach (var datapoint in data)
        {
            if (datapoint.RtEntity == null)
            {
                continue;
            }

            switch (datapoint.ModOption)
            {
                case EntityModOptions.Replace:
                case EntityModOptions.Update:
                case EntityModOptions.Insert:
                    // Same fallback as the raw save: RtId on the EntityUpdateInfo is null on
                    // freshly-shaped inserts.
                    var rtId = datapoint.RtId ?? datapoint.RtEntity.RtId;

                    // Pull the window attributes off the dictionary so they don't surface as
                    // user columns on the target table. Missing or non-DateTime values mean
                    // this row can't represent a time-range — skip with a debug log instead of
                    // aborting the whole batch.
                    var attrs = datapoint.RtEntity.Attributes.ToDictionary();
                    if (!TryPopDateTime(attrs, c.FromAttributePath, out var from) ||
                        !TryPopDateTime(attrs, c.ToAttributePath, out var to))
                    {
                        skippedNoWindow++;
                        continue;
                    }

                    toInsert.Add(new TimeRangeStreamDataPoint
                    {
                        From = from,
                        To = to,
                        RtId = rtId,
                        RtWellKnownName = datapoint.RtEntity.RtWellKnownName,
                        CkTypeId = datapoint.CkTypeId,
                        Attributes = attrs,
                    });
                    break;

                // Deletes are not propagated to time-range archives — same policy as raw archives.
                case EntityModOptions.Delete:
                default:
                    break;
            }
        }

        if (skippedNoWindow > 0)
        {
            nodeContext.Debug(
                $"Skipped {skippedNoWindow} datapoint(s) without a usable [{c.FromAttributePath}, {c.ToAttributePath}) window.");
        }

        if (toInsert.Count != 0)
        {
            // Integrity guard: every distinct source rtId must resolve to an entity that still
            // exists in the rt-model. This catches the orphan-rows bug where datapoints were
            // written for rtIds that never existed as EnergyMeasurement entities.
            await EnsureSourceRtIdsExistAsync(toInsert, archiveRtId);

            nodeContext.Debug(
                $"Inserting {toInsert.Count} time-range data point(s) into archive '{archiveRtId}'");
            await streamDataRepo.InsertTimeRangeAsync(archiveRtId, toInsert);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Validates that every distinct source <c>RtId</c> in the batch resolves to an entity that
    /// exists in the rt-model. Throws an <see cref="InvalidOperationException"/> naming the missing
    /// rtId(s) and the archive if any does not exist, so the orphan-rows class of bug fails loudly
    /// instead of writing datapoints that no entity owns.
    /// </summary>
    private async Task EnsureSourceRtIdsExistAsync(
        IReadOnlyList<TimeRangeStreamDataPoint> toInsert, OctoObjectId archiveRtId)
    {
        // Group the source rtIds by their CkTypeId — the by-id lookup resolves the typed runtime
        // collection from the CK cache, so it needs a concrete ck type (the type-agnostic generic
        // overload throws "CkTypeId not set" on the base RtEntity). Every datapoint's CkTypeId
        // matches the archive's TargetCkTypeId, so in practice this is a single group.
        var rtIdsByType = toInsert
            .Where(p => p.RtId != OctoObjectId.Empty)
            .GroupBy(p => p.CkTypeId.ToString())
            .Select(g => (CkTypeId: g.First().CkTypeId, RtIds: g.Select(p => p.RtId).Distinct().ToList()))
            .ToList();

        if (rtIdsByType.Count == 0)
        {
            return;
        }

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var existingRtIds = new HashSet<OctoObjectId>();
        var requestedRtIds = new List<OctoObjectId>();
        foreach (var (ckTypeId, rtIds) in rtIdsByType)
        {
            requestedRtIds.AddRange(rtIds);
            var existing = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
                session, ckTypeId, rtIds, RtEntityQueryOptions.Create(), 0, rtIds.Count);
            foreach (var e in existing.Items)
            {
                existingRtIds.Add(e.RtId);
            }
        }
        await session.CommitTransactionAsync();

        var missing = requestedRtIds.Distinct().Where(id => !existingRtIds.Contains(id)).ToList();

        if (missing.Count != 0)
        {
            throw new InvalidOperationException(
                $"SaveTimeRangeStreamDataInArchive: {missing.Count} source rtId(s) do not exist as " +
                $"entities in the rt-model and would create orphan rows in archive '{archiveRtId}': " +
                $"{string.Join(", ", missing)}. Refusing to insert.");
        }
    }

    /// <summary>
    /// Extracts a DateTime value from the attribute dictionary by key path, removes it (so it
    /// doesn't reappear as a user column), and normalises common JSON-serialised string variants to
    /// DateTime. Supports nested traversal of record-typed attribute values via dot notation, e.g.
    /// <c>"TimeRange.From"</c> reads <c>From</c> off the <c>TimeRange</c> record and strips that
    /// inner key (leaving the rest of the record in place, which the storage layer will then drop
    /// alongside any other unknown columns). Returns false when the key path can't be resolved or
    /// the leaf value can't be coerced to a DateTime, leaving the dictionary untouched.
    /// </summary>
    private static bool TryPopDateTime(IDictionary<string, object?> attrs, string keyPath, out DateTime value)
    {
        var parts = keyPath.Split('.');
        if (parts.Length == 1)
        {
            return TryPopFromLeaf(attrs, parts[0], out value);
        }

        if (!attrs.TryGetValue(parts[0], out var nested) || nested is null)
        {
            value = default;
            return false;
        }

        switch (nested)
        {
            case RtRecord record:
                return TryPopFromRtRecord(record, parts, 1, out value);
            case IDictionary<string, object?> dict:
                return TryPopFromDictPath(dict, parts, 1, out value);
            default:
                value = default;
                return false;
        }
    }

    private static bool TryPopFromLeaf(IDictionary<string, object?> attrs, string key, out DateTime value)
    {
        if (!attrs.TryGetValue(key, out var raw) || raw is null)
        {
            value = default;
            return false;
        }

        if (TryCoerceDateTime(raw, out value))
        {
            attrs.Remove(key);
            return true;
        }

        value = default;
        return false;
    }

    private static bool TryPopFromDictPath(IDictionary<string, object?> dict, string[] parts, int idx,
        out DateTime value)
    {
        if (idx == parts.Length - 1)
        {
            return TryPopFromLeaf(dict, parts[idx], out value);
        }

        if (!dict.TryGetValue(parts[idx], out var nested) || nested is null)
        {
            value = default;
            return false;
        }

        return nested switch
        {
            RtRecord r => TryPopFromRtRecord(r, parts, idx + 1, out value),
            IDictionary<string, object?> d => TryPopFromDictPath(d, parts, idx + 1, out value),
            _ => Fail(out value)
        };
    }

    private static bool TryPopFromRtRecord(RtRecord record, string[] parts, int idx, out DateTime value)
    {
        // RtRecord.Attributes is IReadOnlyDictionary; we need to mutate via SetAttributeRawValue
        // when stripping the leaf. For the SaveTimeRangeStreamDataInArchive use case the record
        // contains only the window boundaries, so leaving the record-shell behind (after stripping
        // From/To) is fine — the storage layer drops unknown user columns with a WARN log.
        if (!record.Attributes.TryGetValue(parts[idx], out var raw) || raw is null)
        {
            value = default;
            return false;
        }

        if (idx == parts.Length - 1)
        {
            if (TryCoerceDateTime(raw, out value))
            {
                record.SetAttributeRawValue(parts[idx], null);
                return true;
            }

            value = default;
            return false;
        }

        return raw switch
        {
            RtRecord r => TryPopFromRtRecord(r, parts, idx + 1, out value),
            IDictionary<string, object?> d => TryPopFromDictPath(d, parts, idx + 1, out value),
            _ => Fail(out value)
        };
    }

    private static bool TryCoerceDateTime(object raw, out DateTime value)
    {
        switch (raw)
        {
            case DateTime dt:
                value = dt;
                return true;
            case DateTimeOffset dto:
                value = dto.UtcDateTime;
                return true;
            case string s when DateTime.TryParse(
                s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed):
                value = parsed;
                return true;
            default:
                value = default;
                return false;
        }
    }

    private static bool Fail(out DateTime value)
    {
        value = default;
        return false;
    }
}
