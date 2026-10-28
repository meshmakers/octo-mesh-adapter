using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Filters the latest update info for each entity
/// </summary>
/// <remarks>
/// Several inserts of the same RtId (e.g. several readings of a new device in one batch) are collapsed into one
/// insert: the latest insert wins, and plain updates of the same RtId are folded into it attribute by attribute
/// (later values win). Updates of entities without an insert in the batch keep the latest update; deletes keep
/// the latest delete.
/// </remarks>
[NodeConfiguration(typeof(FilterLatestUpdateInfoNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class FilterLatestUpdateInfoNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<FilterLatestUpdateInfoNodeConfiguration>();

        var list = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(c.Path);

        if (list != null && list.Any())
        {
            List<EntityUpdateInfo<RtEntity>> resultUpdateInfos = new();
            // Inserts of the same RtId are collapsed into one insert (latest wins), plain updates of it are folded in
            var foldedUpdates = new HashSet<EntityUpdateInfo<RtEntity>>(ReferenceEqualityComparer.Instance);
            resultUpdateInfos.AddRange(CollapseInserts(list, foldedUpdates));

            // We group all update commands by RtEntityId and take the latest one
            var grouped = list.Where(x => x.ModOption == EntityModOptions.Update && !foldedUpdates.Contains(x)).GroupBy(x => x.GetRtEntityId());
            resultUpdateInfos.AddRange(grouped
                .Select(x => x.OrderByDescending(y => y.RtEntity?.RtChangedDateTime).First()).ToList());

            // We group all delete commands by RtEntityId and take the latest one
            var groupedDelete = list.Where(x => x.ModOption == EntityModOptions.Delete).GroupBy(x => x.GetRtEntityId());
            resultUpdateInfos.AddRange(groupedDelete
                .Select(x => x.OrderByDescending(y => y.RtEntity?.RtChangedDateTime).First()).ToList());

            dataContext.Set(c.TargetPath, resultUpdateInfos, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode);
        }

        await next(dataContext, nodeContext);
    }

    private static List<EntityUpdateInfo<RtEntity>> CollapseInserts(List<EntityUpdateInfo<RtEntity>> list,
        HashSet<EntityUpdateInfo<RtEntity>> foldedUpdates)
    {
        var result = new List<EntityUpdateInfo<RtEntity>>();
        var groups = new Dictionary<(string, OctoObjectId), List<EntityUpdateInfo<RtEntity>>>();

        foreach (var insert in list.Where(x => x.ModOption == EntityModOptions.Insert))
        {
            if (insert.RtEntity == null || insert.RtEntity.RtId.Equals(OctoObjectId.Empty))
            {
                // No identity to collapse on
                result.Add(insert);
                continue;
            }

            var key = (insert.CkTypeId.ToString(), insert.RtEntity.RtId);
            if (!groups.TryGetValue(key, out var group))
            {
                group = new List<EntityUpdateInfo<RtEntity>>();
                groups.Add(key, group);
                result.Add(insert); // placeholder keeps first-appearance order, replaced below
            }

            group.Add(insert);
        }

        for (var i = 0; i < result.Count; i++)
        {
            var first = result[i];
            if (first.RtEntity == null || first.RtEntity.RtId.Equals(OctoObjectId.Empty))
            {
                continue;
            }

            var key = (first.CkTypeId.ToString(), first.RtEntity.RtId);
            var inserts = groups[key];

            // Plain updates of the same entity are folded into the insert; updates with guards, secret clearing or
            // record array item updates keep their own semantics and stay untouched.
            var updates = list.Where(x => x.ModOption == EntityModOptions.Update
                                          && x.RtEntity != null
                                          && (x.RtId ?? x.RtEntity!.RtId).Equals(key.Item2)
                                          && x.CkTypeId.ToString() == key.Item1
                                          && x.UpdateGuard == null
                                          && x.ClearSecretAttributes == null
                                          && x.RecordArrayItemUpdates == null).ToList();

            if (inserts.Count == 1 && updates.Count == 0)
            {
                continue;
            }

            foreach (var update in updates)
            {
                foldedUpdates.Add(update);
            }

            result[i] = Merge(inserts.Concat(updates));
        }

        return result;
    }

    private static EntityUpdateInfo<RtEntity> Merge(IEnumerable<EntityUpdateInfo<RtEntity>> items)
    {
        // Oldest first (stable, so equal or missing timestamps keep the order of the batch); later values win.
        var ordered = items.OrderBy(x => x.RtEntity?.RtChangedDateTime).ToList();
        var latestInsert = ordered.Last(x => x.ModOption == EntityModOptions.Insert);

        if (ordered.Count == 1)
        {
            return latestInsert;
        }

        var latest = latestInsert.RtEntity!;
        var attributes = new Dictionary<string, object?>();
        foreach (var item in ordered)
        {
            foreach (var (name, value) in item.RtEntity!.Attributes)
            {
                attributes[name] = value;
            }
        }

        var lastItem = ordered[^1].RtEntity!;
        var merged = new RtEntity(latest.CkTypeId ?? latestInsert.CkTypeId, latest.RtId, attributes)
        {
            RtCreationDateTime = latest.RtCreationDateTime,
            RtChangedDateTime = lastItem.RtChangedDateTime ?? latest.RtChangedDateTime,
            RtArchivedDateTime = latest.RtArchivedDateTime,
            RtWellKnownName = lastItem.RtWellKnownName ?? latest.RtWellKnownName,
            RtCreatedBy = latest.RtCreatedBy,
            RtDisplayName = lastItem.RtDisplayName ?? latest.RtDisplayName,
            RtDisplayDescription = lastItem.RtDisplayDescription ?? latest.RtDisplayDescription,
            RtVersion = latest.RtVersion,
            RtState = latest.RtState
        };

        return EntityUpdateInfo<RtEntity>.CreateInsert(latestInsert.CkTypeId, merged);
    }
}
