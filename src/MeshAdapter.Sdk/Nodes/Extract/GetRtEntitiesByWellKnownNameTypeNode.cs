using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByWellKnownNameNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByWellKnownNameTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
       var c = nodeContext.GetNodeConfiguration<GetRtEntitiesByWellKnownNameNodeConfiguration>();

        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        var token = dataContext.SelectByPath(c.Path);
        var source = token.ToDictionary(k => k.SelectToken(c.WellKnownNamePath)!.Value<string>()!, v => v);

        var dataQueryOperation = DataQueryOperation.Create();
        dataQueryOperation.AddFieldFilter(nameof(RtEntity.RtWellKnownName), FieldFilterOperator.In, source.Keys);

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId, dataQueryOperation, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        List<string> handledRtWellKnownNames = new();
        foreach (var rtEntity in r.Items)
        {
            if (rtEntity.RtWellKnownName == null)
            {
                continue;
            }
            if (source.TryGetValue(rtEntity.RtWellKnownName, out var sourceToken))
            {
                sourceToken.ReplaceNested(c.RtIdTargetPath, rtEntity.RtId.ToString());
                sourceToken.ReplaceNested(c.CkTypeIdTargetPath, rtEntity.CkTypeId!.ToString());
                sourceToken.ReplaceNested(c.ModOperationPath, (int) UpdateKind.Update);
                handledRtWellKnownNames.Add(rtEntity.RtWellKnownName!);
            }
        }

        if (c.GenerateInsertOperation)
        {
            source.ExceptBy(handledRtWellKnownNames, x => x.Key).ToList().ForEach(x =>
            {
                x.Value.ReplaceNested(c.RtIdTargetPath, OctoObjectId.GenerateNewId().ToString());
                x.Value.ReplaceNested(c.ModOperationPath, (int)UpdateKind.Insert);
                x.Value.ReplaceNested(c.CkTypeIdTargetPath, ckTypeId.ToString());
            });
        }

        await next(dataContext, nodeContext);
    }
}