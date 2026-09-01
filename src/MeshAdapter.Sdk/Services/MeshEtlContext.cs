using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services;

/// <summary>
/// ETL context for the mesh adapter
/// </summary>
public class MeshEtlContext : DefaultEtlContext, IMeshEtlContext
{
    private readonly IPipelineIdentityResolver? _identityResolver;

    /// <summary>
    /// Create a new instance of <see cref="MeshEtlContext"/>
    /// </summary>
    /// <param name="tenantRepository">Tenant repository</param>
    /// <param name="adapterReceivedDateTime">Received date time from the adapter</param>
    /// <param name="tenantId">Tenant identifier</param>
    /// <param name="dataFlowRtId">Data flow runtime identifier</param>
    /// <param name="pipelineExecutionId">Guid that identifies the pipeline execution instance</param>
    /// <param name="pipelineRtEntityId">Pipeline identifier</param>
    /// <param name="externalReceivedDateTime">Date and time when the value was received by an optional external system</param>
    /// <param name="globalConfiguration">Global configuration for the pipeline</param>
    /// <param name="properties">properties that are shared between the different stages of the ETL process and different runs of the pipeline</param>
    /// <param name="verifiedPrincipal">Authenticated caller of the trigger, if any (AB#4975)</param>
    /// <param name="callerAccessToken">
    /// Raw access token the caller presented to the trigger, for nodes that must act as the caller
    /// against another service (delegation / on-behalf-of, AB#5031). Never log it and never write it
    /// into the data context.
    /// </param>
    /// <param name="identityResolver">
    /// Resolves the execution's effective identity for <see cref="GetScopedSessionAsync" /> (AB#5028).
    /// Optional: without one the context can only offer the trigger-verified caller, and falls back to
    /// <see cref="RtSecurityContext.System" /> — the shape a test or a host that builds the context by
    /// hand gets.
    /// </param>
    public MeshEtlContext(string tenantId, ITenantRepository tenantRepository,
        OctoObjectId dataFlowRtId, Guid pipelineExecutionId, RtEntityId pipelineRtEntityId, DateTime adapterReceivedDateTime, DateTime? externalReceivedDateTime,
        IGlobalConfiguration globalConfiguration, IDictionary<string, object?> properties,
        Meshmakers.Octo.Sdk.Common.Services.VerifiedPrincipal? verifiedPrincipal = null,
        string? callerAccessToken = null,
        IPipelineIdentityResolver? identityResolver = null)
        : base(tenantId, dataFlowRtId, pipelineExecutionId, pipelineRtEntityId, adapterReceivedDateTime, externalReceivedDateTime, globalConfiguration, properties, verifiedPrincipal, callerAccessToken)
    {
        TenantRepository = tenantRepository;
        _identityResolver = identityResolver;
    }

    /// <inheritdoc />
    public ITenantRepository TenantRepository { get; }

    /// <inheritdoc />
    public async Task<IOctoSession> GetScopedSessionAsync()
    {
        return await TenantRepository.GetSessionAsync(await ResolveSecurityContextAsync());
    }

    /// <inheritdoc />
    public Task<IOctoSession> GetSystemSessionAsync()
    {
        // Not TenantRepository.GetSessionAsync(): naming RtSecurityContext.System explicitly is what
        // makes "this node is system by decision" visible at the call site and in a test.
        return TenantRepository.GetSessionAsync(RtSecurityContext.System);
    }

    /// <inheritdoc />
    public IOctoSession GetScopedSession()
    {
        return TenantRepository.GetSession(ResolveSecurityContextAsync().AsTask().GetAwaiter().GetResult());
    }

    /// <inheritdoc />
    public IOctoSession GetSystemSession()
    {
        return TenantRepository.GetSession(RtSecurityContext.System);
    }

    private ValueTask<RtSecurityContext> ResolveSecurityContextAsync()
    {
        if (_identityResolver != null)
        {
            return _identityResolver.ResolveAsync();
        }

        // No resolver wired: the caller is still the most specific identity available, and without
        // one there is nothing left but the system context.
        return ValueTask.FromResult(VerifiedPrincipal == null
            ? RtSecurityContext.System
            : RtSecurityContext.ForUser(VerifiedPrincipal.SubjectId, VerifiedPrincipal.Roles));
    }
}
