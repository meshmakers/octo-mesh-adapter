using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline;

/// <summary>
/// Default implementation of the <see cref="IContextCreatorService"/> interface
/// </summary>
// ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
public class MeshContextCreatorService(IServiceProvider serviceProvider, ISystemContext systemContext) : IContextCreatorService
{
    public ITriggerContext CreateTriggerContext(string tenantId, OctoObjectId dataPipelineRtId, RtEntityId pipelineRtEntityId, INodeContext nodeContext)
    {
        return new MeshAdapterTriggerContext(serviceProvider, tenantId, dataPipelineRtId, pipelineRtEntityId, nodeContext);
    }

    /// <inheritdoc />
    public async Task<TContext> CreateEtlContext<TContext>(PipelineRegistration pipelineRegistration,
        ExecutePipelineOptions executePipelineOptions, Guid pipelineExecutionId) where TContext : class, IEtlContext
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(pipelineRegistration.TenantId);

        var context = new MeshEtlContext(pipelineRegistration.TenantId, tenantRepository, pipelineRegistration.DataPipelineRtId,
            pipelineExecutionId,
            pipelineRegistration.PipelineRtEntityId, executePipelineOptions.TransactionStartedDateTime,
            executePipelineOptions.ExternalReceivedDateTime, pipelineRegistration.GlobalConfiguration,
            pipelineRegistration.Dictionary);
        
        var etlContext = context as TContext;
        return etlContext ?? throw PipelineExecutionException.EtlContextTypeMismatch<TContext>(context);
    }
}