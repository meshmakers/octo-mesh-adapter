using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

[NodeConfiguration(typeof(GetAssociationTargetsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class GetAssociationTargetsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetAssociationTargetsNodeConfiguration>();

        var originRtIds = GetOriginRtIds(dataContext, c);
        var originCkTypeId = GetOriginCkTypeId(dataContext, c);

        var targetCkTypeId = GetTargetCkTypeId(dataContext, c);
        var graphDirection = GetGraphDirection(dataContext, c);
        var associationRoleId = GetAssociationRoleId(dataContext, c);

        if (originRtIds == null)
        {
            throw MeshAdapterPipelineExecutionException.OriginRtIdsNotSet(nodeContext);
        }

        if (originCkTypeId == null)
        {
            throw MeshAdapterPipelineExecutionException.OriginCkTypeIdNotSet(nodeContext);
        }

        if (targetCkTypeId == null)
        {
            throw MeshAdapterPipelineExecutionException.TargetCkTypeIdNotSet(nodeContext);
        }

        if (associationRoleId == null)
        {
            throw MeshAdapterPipelineExecutionException.AssociationRoleIdPathNotSet(nodeContext);
        }

        if (graphDirection == null)
        {
            throw MeshAdapterPipelineExecutionException.GraphDirectionNotSet(nodeContext);
        }

        var dataQueryOperation = DataQueryOperation.Create();

        // Add field filters from the configuration
        c.FieldFilters.GetFieldFilter(dataContext, dataQueryOperation);
        c.SortOrders.GetSortOrders(dataContext, dataQueryOperation);

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var result = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(session, originRtIds,
            originCkTypeId, associationRoleId, targetCkTypeId, graphDirection.Value, null, dataQueryOperation);

        if (result.Count == 0)
        {
            nodeContext.Warning("No association target found");
        }

        List<MultipleRtEntityResultDto> resultDto = new List<MultipleRtEntityResultDto>(result.Count);
        foreach (var r in result)
        {
            resultDto.Add(new MultipleRtEntityResultDto
            {
                OriginRtId =  r.Key.RtId,
                OriginCkTypeId = r.Key.CkTypeId,
                TotalCount = r.Value.TotalCount,
                Items = r.Value.Items
            });
        }

        dataContext.SetValueByPath(c.TargetPath, resultDto, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }

    private CkId<CkTypeId>? GetOriginCkTypeId(IDataContext dataContext, GetAssociationTargetsNodeConfiguration config)
    {
        if (config.OriginCkTypeId == null && config.OriginCkTypeIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var sourceCkTypeId = config.OriginCkTypeId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.OriginCkTypeIdPath,
                                 RtNewtonsoftSerializer.DefaultSerializer);

        return sourceCkTypeId;
    }

    private CkId<CkAssociationRoleId>? GetAssociationRoleId(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
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

    private static OctoObjectId[]? GetOriginRtIds(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.OriginRtId == null && config.OriginRtIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(config.OriginRtIdPath))
        {
            var sourceRtIds = dataContext.Current.SelectTokens(config.OriginRtIdPath)
                .Select(t => t.ToObject<OctoObjectId>(RtNewtonsoftSerializer.DefaultSerializer));
            return sourceRtIds.ToArray();
        }

        if (config.OriginRtId != null)
        {
            return [config.OriginRtId.Value];
        }

        return null;
    }

    private static CkId<CkTypeId>? GetTargetCkTypeId(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.TargetCkTypeId == null && config.TargetCkTypeIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var targetCkTypeId = config.TargetCkTypeId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.TargetCkTypeIdPath,
                                 RtNewtonsoftSerializer.DefaultSerializer);

        return targetCkTypeId;
    }

    private static GraphDirections? GetGraphDirection(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.GraphDirection != null)
        {
            return Convert(config.GraphDirection);
        }

        if (config.GraphDirectionPath == null || dataContext.Current == null)
        {
            return null;
        }

        var updateKind =
            dataContext.GetComplexObjectByPath<GraphDirectionsDto?>(config.GraphDirectionPath,
                RtNewtonsoftSerializer.DefaultSerializer);
        return Convert(updateKind);
    }

    private static GraphDirections? Convert(GraphDirectionsDto? directions)
    {
        return directions switch
        {
            GraphDirectionsDto.Inbound => GraphDirections.Inbound,
            GraphDirectionsDto.Outbound => GraphDirections.Outbound,
            GraphDirectionsDto.Any => GraphDirections.Any,
            _ => null
        };
    }
}