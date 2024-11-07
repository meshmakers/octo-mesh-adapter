using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

[NodeConfiguration(typeof(GetOrCreateRtEntitiesByTypeNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class GetOrCreateRtEntitiesByTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<GetOrCreateRtEntitiesByTypeNodeConfiguration>();

        if (c.CkTypeId == null)
        {
            dataContext.NodeContext.Error("CkTypeId is not set");
            return;
        }

        if (c.FieldFilters == null || c.FieldFilters.Count == 0)
        {
            dataContext.NodeContext.Error("FieldFilters is not set");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        foreach (var fieldFilter in c.FieldFilters)
        {
            var value = dataContext.GetSimpleValueByPath<string>(fieldFilter.Path);
            dataQueryOperation.AddFieldFilter(fieldFilter.AttributeName, fieldFilter.Operator, value);
        }

        IOctoSession? session = null;

        try
        {
            session = await etlContext.TenantRepository.GetSessionAsync();
            session.StartTransaction();

            var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, c.CkTypeId,
                dataQueryOperation, 0, 1);

            if (r.TotalCount == 0)
            {
                var objectId = OctoObjectId.GenerateNewId();
                dataContext.SetValueByPath(c.RtIdTargetPath, ValueKind.Simple, WriteMode.Overwrite, objectId);
                dataContext.SetValueByPath(c.CkTypeIdTargetPath, ValueKind.Simple, WriteMode.Overwrite, c.CkTypeId);
                dataContext.SetValueByPath(c.ModOperationPath, ValueKind.Simple, WriteMode.Overwrite, UpdateKind.Insert);
            }
            else
            {
                dataContext.SetValueByPath(c.RtIdTargetPath, ValueKind.Simple, WriteMode.Overwrite,
                    r.Items.First().RtId);
                dataContext.SetValueByPath(c.CkTypeIdTargetPath, ValueKind.Simple, WriteMode.Overwrite, c.CkTypeId);
                dataContext.SetValueByPath(c.ModOperationPath, ValueKind.Simple, WriteMode.Overwrite,
                    UpdateKind.Update);
            }
            await session.CommitTransactionAsync();

        }
        catch (Exception e)
        {
            if (session != null)
            {
                await session.AbortTransactionAsync();
            }

            dataContext.NodeContext.Error(e.Message);
            throw;
        }

        await next(dataContext);
    }
}