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
            // We add all insert statements
            resultUpdateInfos.AddRange(list.Where(x => x.ModOption == EntityModOptions.Insert));

            // We group all update commands by RtEntityId and take the latest one
            var grouped = list.Where(x => x.ModOption == EntityModOptions.Update).GroupBy(x => x.GetRtEntityId());
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
}