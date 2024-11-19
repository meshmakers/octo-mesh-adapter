using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;

/// <summary>
/// Filters the latest update info for each entity
/// </summary>
[NodeConfiguration(typeof(FilterLatestUpdateInfoNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class FilterLatestUpdateInfoNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<FilterLatestUpdateInfoNodeConfiguration>();

        var list = dataContext.GetComplexObjectByPath<List<EntityUpdateInfo<RtEntity>>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

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

            dataContext.SetValueByPath(c.TargetPath, resultUpdateInfos, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }

        await next(dataContext);
    }
}