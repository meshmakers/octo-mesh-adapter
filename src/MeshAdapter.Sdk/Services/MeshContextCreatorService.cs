using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// Default implementation of the <see cref="IContextCreatorService"/> interface
/// </summary>
// ReSharper disable once ClassWithVirtualMembersNeverInherited.Global
internal class MeshContextCreatorService(IServiceProvider serviceProvider, ICkCacheService ckCacheService, ISystemContext systemContext) : IContextCreatorService
{
    public ITriggerContext CreateTriggerContext(string tenantId, OctoObjectId dataFlowRtId,
        RtEntityId pipelineRtEntityId, INodeContext nodeContext, IGlobalConfiguration globalConfiguration)
    {
        return new MeshAdapterTriggerContext(serviceProvider, tenantId, dataFlowRtId, pipelineRtEntityId,
            nodeContext, globalConfiguration);
    }

    /// <inheritdoc />
    public async Task<TContext> CreateEtlContext<TContext>(PipelineRegistration pipelineRegistration,
        ExecutePipelineOptions executePipelineOptions, Guid pipelineExecutionId) where TContext : class, IEtlContext
    {
        var tenantRepository = await systemContext.FindTenantRepositoryAsync(pipelineRegistration.TenantId);
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        // AB#5028: the one point every execution flows through, and therefore the only place the
        // effective identity has to be decided. The resolver is per execution (so one run cannot
        // inherit another's caller) and resolves LAZILY on the first session — an event-triggered
        // pipeline that never touches the repository must not pay a token round trip.
        var identityResolver = new PipelineIdentityResolver(
            pipelineRegistration.TenantId,
            executePipelineOptions.VerifiedPrincipal,
            pipelineRegistration.GlobalConfiguration,
            serviceProvider.GetRequiredService<IServiceAccountTokenService>(),
            serviceProvider.GetRequiredService<ILogger<PipelineIdentityResolver>>());

        var context = new MeshEtlContext(pipelineRegistration.TenantId, tenantRepository, pipelineRegistration.DataFlowRtId,
            pipelineExecutionId,
            pipelineRegistration.PipelineRtEntityId, executePipelineOptions.TransactionStartedDateTime,
            executePipelineOptions.ExternalReceivedDateTime, pipelineRegistration.GlobalConfiguration,
            pipelineRegistration.Dictionary, executePipelineOptions.VerifiedPrincipal,
            // Per-execution side channel. Deliberately NOT put into pipelineRegistration.Dictionary
            // (= IEtlContext.Properties): that dictionary lives on the registration and is shared by
            // every run of the pipeline, so one caller's token would outlive their request (AB#5031).
            executePipelineOptions.CallerAccessToken,
            identityResolver);


        var etlContext = context as TContext;
        return etlContext ?? throw PipelineExecutionException.EtlContextTypeMismatch<TContext>(context);
    }
}