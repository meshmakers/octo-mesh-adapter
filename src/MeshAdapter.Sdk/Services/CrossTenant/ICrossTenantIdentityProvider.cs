using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>Which subject the executing identity of a cross-tenant access is.</summary>
public enum CrossTenantSubjectKind
{
    /// <summary>The verified user caller of the execution; proven with the caller's access token.</summary>
    User,

    /// <summary>The pipeline's service account; proven with a service-account subject in the identity service.</summary>
    ServiceAccount
}

/// <summary>
///     What the identity provider needs to prove a right in the target tenant. Holds credential material (the caller's
///     token); <see cref="ToString" /> never prints it and the object is never logged or stored beyond the call.
/// </summary>
public sealed class CrossTenantProofRequest
{
    /// <param name="sourceTenantId">The pipeline's tenant.</param>
    /// <param name="targetTenantId">The tenant the right is to be proven for.</param>
    /// <param name="subjectKind">User caller or service account.</param>
    /// <param name="sourceIdentity">The identity in the source tenant (subject and roles there; never used in the target).</param>
    /// <param name="callerAccessToken">The caller's access token (user subject only). Credential material.</param>
    /// <param name="globalConfiguration">The pipeline's configuration (holds the service-account configuration).</param>
    public CrossTenantProofRequest(string sourceTenantId, string targetTenantId, CrossTenantSubjectKind subjectKind,
        RtSecurityContext sourceIdentity, string? callerAccessToken, IGlobalConfiguration? globalConfiguration)
    {
        SourceTenantId = sourceTenantId;
        TargetTenantId = targetTenantId;
        SubjectKind = subjectKind;
        SourceIdentity = sourceIdentity;
        CallerAccessToken = callerAccessToken;
        GlobalConfiguration = globalConfiguration;
    }

    /// <summary>The pipeline's tenant.</summary>
    public string SourceTenantId { get; }

    /// <summary>The tenant the right is to be proven for.</summary>
    public string TargetTenantId { get; }

    /// <summary>User caller or service account.</summary>
    public CrossTenantSubjectKind SubjectKind { get; }

    /// <summary>The identity in the source tenant. Its roles are the source tenant's and never reach the target.</summary>
    public RtSecurityContext SourceIdentity { get; }

    /// <summary>The caller's access token for <see cref="CrossTenantSubjectKind.User" />. Never log it.</summary>
    public string? CallerAccessToken { get; }

    /// <summary>The pipeline's configuration, for the service-account path.</summary>
    public IGlobalConfiguration? GlobalConfiguration { get; }

    /// <inheritdoc />
    public override string ToString() =>
        $"{SubjectKind} proof {SourceTenantId} -> {TargetTenantId} (subject {SourceIdentity.SubjectId ?? "(none)"})";
}

/// <summary>
///     A right of the executing identity in <see cref="TenantId" />, proven by a credential the identity service issued
///     for that tenant. Holds no credential.
/// </summary>
/// <param name="TenantId">The tenant the credential was issued for (its <c>tenant_id</c>).</param>
/// <param name="Generation">The tenant generation the credential names (its <c>tenant_gen</c>), or <c>null</c>.</param>
/// <param name="SecurityContext">Subject and roles in <see cref="TenantId" />, from the credential only.</param>
public sealed record CrossTenantProof(string TenantId, string? Generation, RtSecurityContext SecurityContext);

/// <summary>The identity service could not be asked (unreachable, timeout, unusable answer). The guard refuses.</summary>
public sealed class CrossTenantIdentityUnavailableException(string message, Exception? inner = null)
    : Exception(message, inner);

/// <summary>
///     Proves that the executing identity of a pipeline holds a right in another tenant. The identity service is the
///     authority: the provider asks it for a credential issued for the target and returns only subject, roles, tenant
///     and generation of that credential.
/// </summary>
/// <remarks>
///     Contract every implementation keeps:
///     <list type="bullet">
///         <item>Return <c>null</c> when the identity service refuses (no grant, not provisioned, second factor, lockout).</item>
///         <item>Throw <see cref="CrossTenantIdentityUnavailableException" /> when it cannot be asked.</item>
///         <item>Never return a context built from the source identity's roles, and never a system context.</item>
///         <item>Never log, return or cache a token beyond the call.</item>
///     </list>
///     The guard checks tenant and generation of the proof again, so a wrong proof is refused even from a faulty
///     provider.
/// </remarks>
public interface ICrossTenantIdentityProvider
{
    /// <summary>Proves the right in <see cref="CrossTenantProofRequest.TargetTenantId" />.</summary>
    /// <returns>The proof, or <c>null</c> when the identity service refused it.</returns>
    Task<CrossTenantProof?> ProveAsync(CrossTenantProofRequest request, CancellationToken cancellationToken = default);
}
