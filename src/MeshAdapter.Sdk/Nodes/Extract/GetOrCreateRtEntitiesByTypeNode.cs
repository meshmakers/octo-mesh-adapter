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

        var ckTypeId = CkTypeIdHelper.ResolveRtCkTypeId(c.CkTypeId, c.CkTypeIdPath, dataContext, nodeContext);

        if (c.FieldFilters == null || c.FieldFilters.Count == 0)
        {
            nodeContext.Error("FieldFilters is not set");
            return;
        }

        var queryOptions = RtEntityQueryOptions.Create();
        // Add field filters from the configuration
        c.FieldFilters.GetFieldFilter(dataContext, queryOptions);

        IOctoSession? session = null;

        try
        {
            session = await etlContext.TenantRepository.GetSessionAsync();
            session.StartTransaction();

            var r = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(session, ckTypeId,
                queryOptions, 0, 1);

            // Store CkTypeId as a string (SemanticVersionedFullName) so downstream nodes
            // like CreateUpdateInfo can read it via GetSimpleValueByPath<string>.
            var ckTypeIdString = ckTypeId.SemanticVersionedFullName;

            if (r.TotalCount == 0)
            {
                var objectId = OctoObjectId.GenerateNewId();
                dataContext.Set(c.RtIdTargetPath, objectId, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite);
                dataContext.Set(c.CkTypeIdTargetPath, ckTypeIdString, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite);
                dataContext.Set(c.ModOperationPath, UpdateKind.Insert, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite);
            }
            else
            {
                dataContext.Set(c.RtIdTargetPath, r.Items.First().RtId, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite);
                dataContext.Set(c.CkTypeIdTargetPath, ckTypeIdString, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite);
                dataContext.Set(c.ModOperationPath, UpdateKind.Update, DocumentModes.Extend, ValueKinds.Simple,
                    TargetValueWriteModes.Overwrite);
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