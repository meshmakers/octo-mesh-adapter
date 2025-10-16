using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Creates an update item for an existing RtEntity
/// </summary>
[NodeConfiguration(typeof(CreateAssociationUpdateNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class CreateAssociationUpdateNode(NodeDelegate next)
    : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<CreateAssociationUpdateNodeConfiguration>();

        var originRtId = GetOriginRtId(dataContext, nodeContext, c);
        var targetRtId = GetTargetRtId(dataContext, nodeContext, c);
        var updateKind = GetUpdateKind(dataContext, nodeContext, c);
        var roleId = GetAssociationRoleId(dataContext, nodeContext, c);

        var updateItem = updateKind == AssociationUpdateKind.Create
            ? AssociationUpdateInfo.CreateInsert(originRtId, targetRtId, roleId)
            : AssociationUpdateInfo.CreateDelete(originRtId, targetRtId, roleId);

        dataContext.SetValueByPath(c.TargetPath, updateItem, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }

    private RtCkId<CkAssociationRoleId> GetAssociationRoleId(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.AssociationRoleId != null)
        {
            return config.AssociationRoleId;
        }

        if (dataContext.Current == null)
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        if (config.AssociationRoleIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.AssociationRoleIdPathNotSet(nodeContext);
        }

        var roleId = dataContext.GetSimpleValueByPath<RtCkId<CkAssociationRoleId>>(config.AssociationRoleIdPath);
        if (roleId == null)
        {
            throw MeshAdapterPipelineExecutionException.AssociationRoleIdValueNull(nodeContext);
        }

        return roleId;
    }

    private static RtEntityId GetOriginRtId(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.OriginRtId == null && config.OriginRtIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.OriginRtIdNotFound(nodeContext);
        }

        var originCkTypeId = CkTypeIdHelper.ResolveOriginCkTypeId(config.OriginCkTypeId, config.OriginCkTypeIdPath, dataContext, nodeContext);

        if (dataContext.Current == null)
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        var originRtId = config.OriginRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.OriginRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);

        if (originRtId == null)
        {
            throw MeshAdapterPipelineExecutionException.OriginRtIdValueNull(nodeContext);
        }

        return new RtEntityId(originCkTypeId, originRtId.Value);
    }

    private static RtEntityId GetTargetRtId(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.TargetRtId == null && config.TargetRtIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetRtIdNotFound(nodeContext);
        }

        var targetCkTypeId = CkTypeIdHelper.ResolveTargetCkTypeId(config.TargetCkTypeId, config.TargetCkTypeIdPath, dataContext, nodeContext);

        if (dataContext.Current == null)
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        var targetRtId = config.TargetRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.TargetRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);

        if (targetRtId == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetRtIdValueNull(nodeContext);
        }

        return new RtEntityId(targetCkTypeId, targetRtId.Value);
    }

    private static AssociationUpdateKind GetUpdateKind(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.UpdateKind != null)
        {
            return config.UpdateKind.Value;
        }

        if (config.UpdateKindPath == null)
        {
            throw MeshAdapterPipelineExecutionException.UpdateKindPathNotFound(nodeContext);
        }

        if (dataContext.Current == null)
        {
            throw PipelineExecutionException.InputValueNull(nodeContext);
        }

        var updateKind =
            dataContext.GetComplexObjectByPath<AssociationUpdateKind?>(config.UpdateKindPath,
                RtNewtonsoftSerializer.DefaultSerializer);

        if (updateKind == null)
        {
            throw MeshAdapterPipelineExecutionException.UpdateKindNull(nodeContext);
        }

        return updateKind.Value;
    }
}