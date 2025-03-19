using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

[NodeConfiguration(typeof(GetAssociationTargetsNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class GetAssociationTargetsNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetAssociationTargetsNodeConfiguration>();

        var sourceRtId = GetSourceObjectId(dataContext, c);
        var sourceCkTypeId = GetSourceCkTypeId(dataContext, c);

        var targetCkTypeId = GetTargetCkTypeId(dataContext, c);
        var graphDirection = GetGraphDirection(dataContext, c);
        var roleId = GetAssociationRoleId(dataContext, c);

        if (sourceRtId == null)
        {
            nodeContext.Error("sourceRtId is not set");
            return;
        }

        if (sourceCkTypeId == null)
        {
            nodeContext.Error("sourceCkTypeId is not set");
            return;
        }

        if (targetCkTypeId == null)
        {
            nodeContext.Error("targetRtId is not set");
            return;
        }

        if (roleId == null)
        {
            nodeContext.Error("roleId is not set");
            return;
        }

        if (graphDirection == null)
        {
            nodeContext.Error("graph direction is not set");
            return;
        }

        var query = DataQueryOperation.Create();

        if (c.FieldFilters != null && c.FieldFilters.Any())
        {
            foreach (var f in c.FieldFilters)
            {
                query.AddFieldFilter(f.AttributePath, GetOperator(f.Operator), f.ComparisonValue);
            }
        }

        if (c.SortOrders != null && c.SortOrders.Any())
        {
            foreach (var s in c.SortOrders)
            {
                query.SortOrder(s.AttributeName, GetSortOrder(s.SortOrder));
            }
        }

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        var result = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(session, [sourceRtId.Value],
            sourceCkTypeId, roleId, targetCkTypeId, graphDirection.Value, null, query, 0, 1);

        if (result.Count == 0)
        {
            nodeContext.Error("No association target found");
            return;
        }

        var entity = result.Values.Single().Items.Single();

        dataContext.SetValueByPath(c.TargetPath, entity, c.DocumentMode, c.TargetValueKind,
            c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);

        await next(dataContext, nodeContext);
    }

    private SortOrders GetSortOrder(SortOrdersDto sortOrder)
    {
        return sortOrder switch
        {
            SortOrdersDto.Ascending => SortOrders.Ascending,
            SortOrdersDto.Descending => SortOrders.Descending,
            SortOrdersDto.Default => SortOrders.Default,
            _ => throw new ArgumentOutOfRangeException(nameof(sortOrder), sortOrder, null)
        };
    }

    private FieldFilterOperator GetOperator(FieldFilterOperatorDto f)
    {
        return f switch
        {
            FieldFilterOperatorDto.Equals => FieldFilterOperator.Equals,
            FieldFilterOperatorDto.NotEquals => FieldFilterOperator.NotEquals,
            FieldFilterOperatorDto.LessThan => FieldFilterOperator.LessThan,
            FieldFilterOperatorDto.LessEqualThan => FieldFilterOperator.LessEqualThan,
            FieldFilterOperatorDto.GreaterThan => FieldFilterOperator.GreaterThan,
            FieldFilterOperatorDto.GreaterEqualThan => FieldFilterOperator.GreaterEqualThan,
            FieldFilterOperatorDto.In => FieldFilterOperator.In,
            FieldFilterOperatorDto.NotIn => FieldFilterOperator.NotIn,
            FieldFilterOperatorDto.Like => FieldFilterOperator.Like,
            FieldFilterOperatorDto.MatchRegEx => FieldFilterOperator.MatchRegEx,
            FieldFilterOperatorDto.AnyEq => FieldFilterOperator.AnyEq,
            _ => throw new ArgumentOutOfRangeException(nameof(f), f, null)
        };
    }

    private CkId<CkTypeId>? GetSourceCkTypeId(IDataContext dataContext, GetAssociationTargetsNodeConfiguration config)
    {
        if (config.SourceCkTypeId == null && config.SourceCkTypeIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var sourceCkTypeId = config.SourceCkTypeId ??
                             dataContext.GetComplexObjectByPath<CkId<CkTypeId>?>(config.SourceCkTypeIdPath,
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

    private static OctoObjectId? GetSourceObjectId(IDataContext dataContext,
        GetAssociationTargetsNodeConfiguration config)
    {
        if (config.SourceRtId == null && config.SourceRtIdPath == null || dataContext.Current == null)
        {
            return null;
        }

        var sourceRtId = config.SourceRtId ??
                         dataContext.GetComplexObjectByPath<OctoObjectId?>(config.SourceRtIdPath,
                             RtNewtonsoftSerializer.DefaultSerializer);

        return sourceRtId;
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