using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     The tenant a write batch is judged against by the write guards (<see cref="GenericCreateGuard" />,
///     <see cref="RtAttributeAccessGuard" />): its CK model (replaced creates, attribute access, identity-owned writes) and
///     the method run that may write MethodOnly attributes.
/// </summary>
/// <remarks>
///     Invariant (AB#9769): a batch for another tenant is judged with that tenant's CK model and with <b>no</b> method
///     run. A method run belongs to the pipeline's own tenant; it is never a method run of a type in another tenant, so a
///     cross-tenant write of a MethodOnly attribute, an insert of a type whose method replaces the generic create, and an
///     identity association edge are always refused.
/// </remarks>
internal sealed class WriteGuardScope
{
    private WriteGuardScope(string tenantId, ITenantRepository repository, ExecutePipelineMethodInvocation? marker,
        string pipelineRtId)
    {
        TenantId = tenantId;
        Repository = repository;
        MethodInvocation = marker;
        PipelineRtId = pipelineRtId;
    }

    /// <summary>The tenant whose CK model judges the batch.</summary>
    public string TenantId { get; }

    /// <summary>The repository of <see cref="TenantId" /> (to load its CK cache).</summary>
    public ITenantRepository Repository { get; }

    /// <summary>The method run marker, <c>null</c> outside a method run and always for another tenant.</summary>
    public ExecutePipelineMethodInvocation? MethodInvocation { get; }

    /// <summary>The running pipeline's id, compared with the marker.</summary>
    public string PipelineRtId { get; }

    /// <summary>The pipeline's own tenant with the run's method marker (every write before AB#9769).</summary>
    public static WriteGuardScope Own(IMeshEtlContext etlContext)
    {
        ArgumentNullException.ThrowIfNull(etlContext);
        return new WriteGuardScope(etlContext.TenantId, etlContext.TenantRepository, etlContext.MethodInvocation,
            etlContext.PipelineRtEntityId.RtId.ToString());
    }

    /// <summary>A target the cross-tenant guard opened: its CK model, never a method run.</summary>
    public static WriteGuardScope CrossTenant(IMeshEtlContext etlContext, ICrossTenantAccess access)
    {
        ArgumentNullException.ThrowIfNull(etlContext);
        ArgumentNullException.ThrowIfNull(access);
        return new WriteGuardScope(access.TenantId, access.Repository, null,
            etlContext.PipelineRtEntityId.RtId.ToString());
    }
}
