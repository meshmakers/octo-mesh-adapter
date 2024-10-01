using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

[NodeConfiguration(typeof(FindOrCreateRtIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class FindOrCreateRtIdNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<FindOrCreateRtIdNodeConfiguration>();

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

        IResultSet<RtEntity> r;

        try
        {
            var session = await etlContext.TenantRepository.GetSessionAsync();
            session.StartTransaction();

            r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, c.CkTypeId,
                dataQueryOperation, 0, 1);
            await session.CommitTransactionAsync();
        }
        catch (Exception e)
        {
            dataContext.NodeContext.Error(e.Message);
            throw;
        }


        if (r.TotalCount == 0)
        {
            var objectId = OctoObjectId.GenerateNewId();
            dataContext.SetValueByPath("$.rtId", ValueKind.Simple, WriteMode.Overwrite, objectId);
            dataContext.SetValueByPath("$.modOperation", ValueKind.Simple, WriteMode.Overwrite, UpdateKind.Insert);
        }
        else
        {
            dataContext.SetValueByPath("$.rtId", ValueKind.Simple, WriteMode.Overwrite, r.Items.First().RtId);
            dataContext.SetValueByPath("$.modOperation", ValueKind.Simple, WriteMode.Overwrite, UpdateKind.Update);
        }

        await next(dataContext);
    }
}