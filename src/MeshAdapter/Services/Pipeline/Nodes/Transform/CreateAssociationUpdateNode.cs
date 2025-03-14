using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;

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

        var sourceRtId = GetSourceRtId(dataContext, nodeContext, c);
        var targetRtId = GetTargetRtId(dataContext, nodeContext, c);
        var updateKind = GetUpdateKind(dataContext, nodeContext, c);
        var roleId = GetAssociationRoleId(dataContext, nodeContext, c);

        var updateItem = updateKind == AssociationUpdateKind.Create
            ? AssociationUpdateInfo.CreateCreate(sourceRtId, targetRtId, roleId)
            : AssociationUpdateInfo.CreateDelete(sourceRtId, targetRtId, roleId);

        dataContext.SetValueByPath(c.TargetPath, updateItem, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }

    private CkId<CkAssociationRoleId> GetAssociationRoleId(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.AssociationRoleId != null)
        {
            return config.AssociationRoleId;
        }

        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        if (config.AssociationRoleIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.AssociationRoleIdPathNotFound(nodeContext);
        }

        var roleId = dataContext.GetSimpleValueByPath<CkId<CkAssociationRoleId>>(config.AssociationRoleIdPath);
        if (roleId == null)
        {
            throw MeshAdapterPipelineExecutionException.AssociationRoleIdValueNull(nodeContext);
        }

        return roleId;
    }

    private static RtEntityId GetSourceRtId(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.SourceRtId == null && config.SourceRtIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.SourceRtIdNotFound(nodeContext);
        }

        if (config.SourceCkTypeId == null && config.SourceCkTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.SourceCkTypeIdNotFound(nodeContext);
        }

        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        var sourceRtId = config.SourceRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.SourceRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);


        var sourceCkTypeId = config.SourceCkTypeId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.SourceCkTypeIdPath,
                                 RtNewtonsoftSerializer.DefaultSerializer);

        if (sourceRtId == null)
        {
            throw MeshAdapterPipelineExecutionException.SourceRtIdValueNull(nodeContext);
        }

        if (sourceCkTypeId == null)
        {
            throw MeshAdapterPipelineExecutionException.SourceCkTypeIdValueNull(nodeContext);
        }

        return new RtEntityId(sourceCkTypeId, sourceRtId.Value);
    }

    private static RtEntityId GetTargetRtId(IDataContext dataContext, INodeContext nodeContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.TargetRtId == null && config.TargetRtIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetRtIdNotFound(nodeContext);
        }

        if (config.SourceCkTypeId == null && config.SourceCkTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.SourceCkTypeIdNotFound(nodeContext);
        }

        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        var targetRtId = config.TargetRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.TargetRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);

        var targetCkTypeId = config.TargetCkId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.TargetCkTypeIdPath,
                                 RtNewtonsoftSerializer.DefaultSerializer);

        if (targetRtId == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetRtIdValueNull(nodeContext);
        }

        if (targetCkTypeId == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetCkTypeIdValueNull(nodeContext);
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
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
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