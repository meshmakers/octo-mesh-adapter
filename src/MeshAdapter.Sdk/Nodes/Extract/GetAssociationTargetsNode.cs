using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
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

        var queryOptions = RtEntityQueryOptions.Create();

        // Add field filters from the configuration
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);
        c.SortOrders.GetSortOrders(queryOptions);

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var result = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(session, originRtIds,
            originCkTypeId, associationRoleId, targetCkTypeId, graphDirection.Value, null, queryOptions);

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

        dataContext.Set(c.TargetPath, resultDto, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private RtCkId<CkTypeId>? GetOriginCkTypeId(IDataContext dataContext, GetAssociationTargetsNodeConfiguration config)
    {
        if (config.OriginCkTypeId == null && config.OriginCkTypeIdPath == null)
        {
            return null;
        }

        var sourceCkTypeId = config.OriginCkTypeId ??
                             dataContext.Get<RtCkId<CkTypeId>?>(config.OriginCkTypeIdPath!);

        return sourceCkTypeId;
    }

    private RtCkId<CkAssociationRoleId>? GetAssociationRoleId(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.AssociationRoleId != null)
        {
            return config.AssociationRoleId;
        }

        if (config.AssociationRoleIdPath == null)
        {
            return null;
        }

        var roleId = dataContext.Get<RtCkId<CkAssociationRoleId>>(config.AssociationRoleIdPath);
        return roleId;
    }

    private static OctoObjectId[]? GetOriginRtIds(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.OriginRtId == null && config.OriginRtIdPath == null)
        {
            return null;
        }

        if (!string.IsNullOrWhiteSpace(config.OriginRtIdPath))
        {
            // SelectMatches captures the full JSONPath dialect, including wildcards
            // (e.g. "$.items[*].rtId"). The previous Get<OctoObjectId?>(path) returned
            // only the first match, silently collapsing N-element wildcard expansions
            // to a single origin id. Same fix shape as FieldFilterExtensions (#5).
            var matches = dataContext.SelectMatches(config.OriginRtIdPath).ToList();
            if (matches.Count == 0)
            {
                return null;
            }

            // Special-case the single literal array (e.g. "$.rtIds" where the value
            // is itself a JSON array) — unwrap it into its OctoObjectId elements.
            if (matches.Count == 1 && matches[0].GetKind("$") == DataKind.Array)
            {
                var arr = matches[0].GetArray<OctoObjectId>("$");
                return arr?.Where(x => x != OctoObjectId.Empty).ToArray();
            }

            var ids = matches
                .Select(m => m.Get<OctoObjectId?>("$"))
                .Where(id => id.HasValue && id.Value != OctoObjectId.Empty)
                .Select(id => id!.Value)
                .ToArray();
            return ids.Length > 0 ? ids : null;
        }

        if (config.OriginRtId != null)
        {
            return [config.OriginRtId.Value];
        }

        return null;
    }

    private static RtCkId<CkTypeId>? GetTargetCkTypeId(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.TargetCkTypeId == null && config.TargetCkTypeIdPath == null)
        {
            return null;
        }

        var targetCkTypeId = config.TargetCkTypeId ??
                             dataContext.Get<RtCkId<CkTypeId>?>(config.TargetCkTypeIdPath!);

        return targetCkTypeId;
    }

    private static GraphDirections? GetGraphDirection(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.GraphDirection != null)
        {
            return Convert(config.GraphDirection);
        }

        if (config.GraphDirectionPath == null)
        {
            return null;
        }

        var updateKind =
            dataContext.Get<GraphDirectionsDto?>(config.GraphDirectionPath);
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