using GraphQL.Client.Abstractions.Utilities;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(GetRtEntitiesByIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetRtEntitiesByIdNode(NodeDelegate next, IMeshEtlContext context) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var etlContext = context;

        var c = nodeContext.GetNodeConfiguration<GetRtEntitiesByIdNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            nodeContext.Error("CkTypeId is not set");
            return;
        }

        if (c.RtIds == null)
        {
            nodeContext.Error("RtIds is not set");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        if (c.FieldFilters != null)
        {
            foreach (var fieldFilter in c.FieldFilters)
            {
                dataQueryOperation.AddFieldFilter(fieldFilter.AttributePath.ToPascalCase(), (FieldFilterOperator) fieldFilter.Operator,
                    fieldFilter.ComparisonValue);
            }
        }

        var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();
        var r = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(session, c.CkTypeId, c.RtIds.ToList(),
            dataQueryOperation, c.Skip, c.Take);
        await session.CommitTransactionAsync();

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, r);

        await next(dataContext, nodeContext);
    }
}