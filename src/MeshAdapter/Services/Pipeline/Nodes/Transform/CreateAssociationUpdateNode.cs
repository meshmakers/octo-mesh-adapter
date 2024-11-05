using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

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
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<CreateAssociationUpdateNodeConfiguration>();

        var sourceRtId = GetSourceRtId(dataContext, c);
        var targetRtId = GetTargetRtId(dataContext, c);
        var updateKind = GetUpdateKind(dataContext, c);
        var roleId = GetAssociationRoleId(dataContext, c);

        if (sourceRtId == null)
        {
            dataContext.NodeContext.Error("sourceRtId is not set");
            return;
        }

        if (targetRtId == null)
        {
            dataContext.NodeContext.Error("targetRtId is not set");
            return;
        }

        if (roleId == null)
        {
            dataContext.NodeContext.Error("roleId is not set");
            return;
        }

        if (updateKind == null)
        {
            dataContext.NodeContext.Error("update kind is not set");
            return;
        }

        var updateItem = updateKind == AssociationUpdateKind.Create
            ? AssociationUpdateInfo.CreateCreate(sourceRtId.Value, targetRtId.Value, roleId)
            : AssociationUpdateInfo.CreateDelete(sourceRtId.Value, targetRtId.Value, roleId);


        dataContext.SetValueByPath(c.TargetPath, updateItem, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);


        await next(dataContext);
    }

    private CkId<CkAssociationRoleId>? GetAssociationRoleId(IDataContext dataContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.AssociationRoleId != null)
        {
            return config.AssociationRoleId;
        }

        if (config.AssociationRoleIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var roleId = dataContext.GetSimpleValueByPath<CkId<CkAssociationRoleId>>(config.AssociationRoleIdPath);
        return roleId;
    }

    private static RtEntityId? GetSourceRtId(IDataContext dataContext, CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.SourceRtId == null && config.SourceRtIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var sourceRtId = config.SourceRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.SourceRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);

        if (config.SourceCkId == null && config.SourceCkTypeIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var sourceCkTypeId = config.SourceCkId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.SourceCkTypeIdPath,
                                 RtNewtonsoftSerializer.DefaultSerializer);

        if (sourceRtId == null || sourceCkTypeId == null)
        {
            return null;
        }

        return new RtEntityId(sourceCkTypeId, sourceRtId.Value);
    }

    private static RtEntityId? GetTargetRtId(IDataContext dataContext, CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.TargetRtId == null && config.TargetRtIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var targetRtId = config.TargetRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.TargetRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);

        if (config.TargetCkId == null && config.TargetCkTypeIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var targetCkTypeId = config.TargetCkId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.TargetCkTypeIdPath,
                                 RtNewtonsoftSerializer.DefaultSerializer);

        if (targetRtId == null || targetCkTypeId == null)
        {
            return null;
        }

        return new RtEntityId(targetCkTypeId, targetRtId.Value);
    }

    private static AssociationUpdateKind? GetUpdateKind(IDataContext dataContext,
        CreateAssociationUpdateNodeConfiguration config)
    {
        if (config.UpdateKind != null)
        {
            return config.UpdateKind;
        }

        if (config.UpdateKindPath == null || dataContext.Current == null)
        {
            return null;
        }

        var updateKind =
            dataContext.GetComplexObjectByPath<AssociationUpdateKind?>(config.UpdateKindPath,
                RtNewtonsoftSerializer.DefaultSerializer);
        return updateKind;
    }
}