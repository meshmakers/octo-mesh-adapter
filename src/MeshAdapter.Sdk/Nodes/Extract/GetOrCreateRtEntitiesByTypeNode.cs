using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

[NodeConfiguration(typeof(GetOrCreateRtEntitiesByTypeNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class GetOrCreateRtEntitiesByTypeNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetOrCreateRtEntitiesByTypeNodeConfiguration>();

        var ckTypeId = CkTypeIdHelper.ResolveCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        if (c.FieldFilters == null || c.FieldFilters.Count == 0)
        {
            nodeContext.Error("FieldFilters is not set");
            return;
        }

        var dataQueryOperation = DataQueryOperation.Create();
        // Add field filters from the configuration
        c.FieldFilters.GetFieldFilter(dataContext, dataQueryOperation);

        IOctoSession? session = null;

        try
        {
            session = await etlContext.TenantRepository.GetSessionAsync();
            session.StartTransaction();

            var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId,
                dataQueryOperation, 0, 1);

            if (r.TotalCount == 0)
            {
                var objectId = OctoObjectId.GenerateNewId();
                dataContext.SetValueByPath(c.RtIdTargetPath, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite, objectId);
                dataContext.SetValueByPath(c.CkTypeIdTargetPath, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite, ckTypeId);
                dataContext.SetValueByPath(c.ModOperationPath, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite, UpdateKind.Insert);
            }
            else
            {
                dataContext.SetValueByPath(c.RtIdTargetPath, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite,
                    r.Items.First().RtId);
                dataContext.SetValueByPath(c.CkTypeIdTargetPath, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite, ckTypeId);
                dataContext.SetValueByPath(c.ModOperationPath, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite,
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

            nodeContext.Error(e.Message);
            throw;
        }

        await next(dataContext, nodeContext);
    }
}