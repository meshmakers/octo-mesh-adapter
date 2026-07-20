using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Deploys a specific pipeline within the same data flow to its assigned adapter
/// via the Communication Controller API. Reads the pipeline's definition and adapter
/// assignment from the runtime repository, validates the target is within the same
/// data flow and is not the currently executing pipeline.
/// </summary>
[NodeConfiguration(typeof(DeployPipelineNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class DeployPipelineNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    ICommunicationServicesClient communicationServicesClient,
    IServiceAccountTokenService serviceAccountTokenService) : IPipelineNode
{    private static readonly RtCkId<CkTypeId> PipelineCkTypeId = new("System.Communication/Pipeline");
    private static readonly RtCkId<CkAssociationRoleId> ExecutesRoleId = new("System.Communication/Executes");
    private static readonly RtCkId<CkAssociationRoleId> ParentChildRoleId = new("System/ParentChild");
    private static readonly RtCkId<CkTypeId> AdapterCkTypeId = new("System.Communication/Adapter");
    private static readonly RtCkId<CkTypeId> DataFlowCkTypeId = new("System.Communication/DataFlow");

    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<DeployPipelineNodeConfiguration>();

        var targetPipelineRtId = GetPipelineRtId(dataContext, c);
        if (targetPipelineRtId == null)
        {
            nodeContext.Error("Pipeline RtId is not set. Provide PipelineRtId or PipelineRtIdPath.");
            return;
        }

        // Safety: must not deploy itself
        var currentPipelineRtId = etlContext.PipelineRtEntityId.RtId;
        if (targetPipelineRtId.Value == currentPipelineRtId)
        {
            nodeContext.Error("Cannot deploy the currently executing pipeline.");
            return;
        }

        if (nodeContext.PipelineExecutionMode?.IsDryRun == true)
        {
            nodeContext.RecordDryRunIntent(DryRunHonouredLoadNodes.DeployPipeline, new
            {
                targetPipelineRtId = targetPipelineRtId.Value.ToString(),
                currentDataFlowRtId = etlContext.DataFlowRtId.ToString(),
                serviceAccountConfigName = c.ServiceAccountConfigName
            });
            await next(dataContext, nodeContext);
            return;
        }

        using var session = await etlContext.TenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Load the target pipeline entity
        var pipelineResult = await etlContext.TenantRepository.GetRtEntitiesByIdAsync(
            session, PipelineCkTypeId, new[] { targetPipelineRtId.Value },
            RtEntityQueryOptions.Create());

        var pipelineEntity = pipelineResult.Items.FirstOrDefault();
        if (pipelineEntity == null)
        {
            nodeContext.Error($"Pipeline {targetPipelineRtId} not found.");
            return;
        }

        // Validate: target pipeline must be in the same data flow
        var currentDataFlowRtId = etlContext.DataFlowRtId;
        var targetDataFlowResults = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(
            session, new[] { targetPipelineRtId.Value }, PipelineCkTypeId,
            ParentChildRoleId, DataFlowCkTypeId, GraphDirections.Outbound, null,
            RtEntityQueryOptions.Create());

        var targetDataFlowRtId = targetDataFlowResults
            .SelectMany(r => r.Value.Items)
            .FirstOrDefault()?.RtId;

        if (targetDataFlowRtId == null || targetDataFlowRtId != currentDataFlowRtId)
        {
            nodeContext.Error(
                $"Pipeline {targetPipelineRtId} does not belong to the current data flow {currentDataFlowRtId}.");
            return;
        }

        // Read pipeline definition
        var pipelineDefinition = pipelineEntity.GetAttributeValueOrDefault("PipelineDefinition") as string;
        if (string.IsNullOrWhiteSpace(pipelineDefinition))
        {
            nodeContext.Error($"Pipeline {targetPipelineRtId} has no pipeline definition.");
            return;
        }

        // Get the adapter assigned via Executes association
        var adapterResults = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(
            session, new[] { targetPipelineRtId.Value }, PipelineCkTypeId,
            ExecutesRoleId, AdapterCkTypeId, GraphDirections.Outbound, null,
            RtEntityQueryOptions.Create());

        var adapterEntity = adapterResults
            .SelectMany(r => r.Value.Items)
            .FirstOrDefault();

        if (adapterEntity == null)
        {
            nodeContext.Error($"Pipeline {targetPipelineRtId} has no assigned adapter (Executes association).");
            return;
        }

        // Ensure we have a valid access token for the Communication Controller REST API
        await serviceAccountTokenService.EnsureTokenAsync(etlContext.TenantRepository, etlContext.TenantId, c.ServiceAccountConfigName);

        // Deploy the pipeline
        nodeContext.Debug($"Deploying pipeline {targetPipelineRtId} to adapter {adapterEntity.RtId}");

        await communicationServicesClient.DeployPipelineAsync(
            adapterEntity.RtId.ToString(),
            targetPipelineRtId.Value.ToString(),
            pipelineDefinition);

        nodeContext.Info($"Pipeline {targetPipelineRtId} deployed successfully to adapter {adapterEntity.RtId}");

        await next(dataContext, nodeContext);
    }

    private static OctoObjectId? GetPipelineRtId(IDataContext dataContext, DeployPipelineNodeConfiguration config)
    {
        if (config.PipelineRtId != null)
        {
            return config.PipelineRtId.Value;
        }

        if (config.PipelineRtIdPath == null)
        {
            return null;
        }

        return dataContext.Get<OctoObjectId?>(config.PipelineRtIdPath);
    }
}
