using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
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

        var data = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

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
            nodeContext.Debug(
                $"Inserting {toInsert.Count} time-range data point(s) into archive '{archiveRtId}'");
            await streamDataRepo.InsertTimeRangeAsync(archiveRtId, toInsert);
        }

        await next(dataContext, nodeContext);
    }

    /// <summary>
    /// Extracts a DateTime value from the attribute dictionary by key, removes it (so it doesn't
    /// reappear as a user column), and normalises common JSON-serialised string variants to
    /// DateTime. Returns false when the key is absent or the value can't be coerced to a
    /// DateTime, leaving the dictionary untouched.
    /// </summary>
    private static bool TryPopDateTime(IDictionary<string, object?> attrs, string key, out DateTime value)
    {
        if (!attrs.TryGetValue(key, out var raw) || raw is null)
        {
            value = default;
            return false;
        }

        switch (raw)
        {
            case DateTime dt:
                attrs.Remove(key);
                value = dt;
                return true;
            case DateTimeOffset dto:
                attrs.Remove(key);
                value = dto.UtcDateTime;
                return true;
            case string s when DateTime.TryParse(
                s, System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                out var parsed):
                attrs.Remove(key);
                value = parsed;
                return true;
            default:
                value = default;
                return false;
        }
    }
}
