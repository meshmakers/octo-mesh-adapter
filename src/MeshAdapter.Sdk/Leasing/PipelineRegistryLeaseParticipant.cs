using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Leasing;

/// <summary>
///     Drops every pipeline registration of the borrowing tenant when the lease ends (AB#4924).
/// </summary>
/// <remarks>
///     <para>
///         🔴 A registration is not merely a name: <c>PipelineRegistration</c> carries the pipeline's
///         <c>GlobalConfiguration</c>, which is where the borrower's service-account credentials live
///         once a pipeline is deployed (AB#5027, and the reason a rotated secret needs a redeploy).
///         Leaving it behind would leave one borrower's credentials in a process about to serve the
///         next one — the single worst thing this participant exists to prevent.
///     </para>
///     <para>
///         The registry is keyed <c>(tenantId, rtEntityId)</c>, so it is isolation-safe by
///         construction: a lookup for tenant B can never return tenant A's registration. What it is
///         not is <b>bounded</b> — which is exactly the same shape as the CK model cache, and has the
///         same answer.
///     </para>
/// </remarks>
internal sealed class PipelineRegistryLeaseParticipant(
    IPipelineRegistryService pipelineRegistryService,
    ILogger<PipelineRegistryLeaseParticipant> logger) : IAdapterLeaseParticipant
{
    /// <inheritdoc />
    public Task EnterLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        // Nothing to do: the pipelines of the leased tenant are registered by the work item, which is
        // the only thing that knows which ones the lease was granted for.
        return Task.CompletedTask;
    }

    /// <inheritdoc />
    public async Task LeaveLeaseAsync(LeaseDto lease, CancellationToken cancellationToken)
    {
        await pipelineRegistryService.UnregisterAllPipelinesAsync(lease.TenantId);
        logger.LogDebug("Pipeline registrations dropped for released tenant '{TenantId}'", lease.TenantId);
    }
}
