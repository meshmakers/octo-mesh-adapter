using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>Stable error code of a refused cross-tenant access. Safe to match on in a client.</summary>
public static class CrossTenantAccessCodes
{
    /// <summary>A node was refused access to another tenant.</summary>
    public const string Forbidden = "CROSS_TENANT_FORBIDDEN";
}

/// <summary>
///     Why the guard refused a cross-tenant access. The tokens are stable, but they are written to the log only: the
///     message a pipeline sees is the same for every refusal of a data-driven target, so a pipeline cannot probe which
///     tenant ids exist or which grants they hold (abuse case "enumeration").
/// </summary>
public static class CrossTenantRefusalReason
{
    /// <summary>The target is registered but not in the relation the node names, or source or target is the system tenant.</summary>
    public const string RelationNotAllowed = "relation_not_allowed";

    /// <summary>The target is not in the tenant registry (unknown, deleted or detached).</summary>
    public const string TargetNotRegistered = "target_not_registered";

    /// <summary>The identity service did not prove a right of the executing identity in the target.</summary>
    public const string IdentityNotProvable = "identity_not_provable";

    /// <summary>The node identity is the system context, or the identity fallback would end there.</summary>
    public const string SystemIdentityRefused = "system_identity_refused";

    /// <summary>The proof belongs to an earlier tenant with the same id (generation differs).</summary>
    public const string TenantGenerationMismatch = "tenant_generation_mismatch";

    /// <summary>Registry, identity service, generation store or the target's repository could not be read (fail closed).</summary>
    public const string TargetUnverifiable = "target_unverifiable";

    /// <summary>The target-tenant setting of the node is inconsistent, or a data-driven id is not a usable string.</summary>
    public const string InvalidTargetConfiguration = "invalid_target_configuration";
}

/// <summary>
///     A node was refused access to another tenant. Carries <see cref="CrossTenantAccessCodes.Forbidden" />; the
///     <see cref="ReasonCode" /> is for the log and for tests, never part of the message.
/// </summary>
public sealed class CrossTenantAccessDeniedException : PipelineExecutionException
{
    private CrossTenantAccessDeniedException(string message, string reasonCode)
        : base(message)
    {
        ReasonCode = reasonCode;
    }

    /// <summary>The stable error code, <see cref="CrossTenantAccessCodes.Forbidden" />.</summary>
    public string ErrorCode => CrossTenantAccessCodes.Forbidden;

    /// <summary>The <see cref="CrossTenantRefusalReason" /> token.</summary>
    public string ReasonCode { get; }

    /// <summary>
    ///     The uniform refusal: the same text whatever the reason, so the answer does not tell whether a tenant exists,
    ///     lies outside the relation or holds no right for the identity.
    /// </summary>
    internal static CrossTenantAccessDeniedException Forbidden(string nodePath, string relation, string reasonCode)
    {
        return new CrossTenantAccessDeniedException(
            $"[{nodePath}]: {CrossTenantAccessCodes.Forbidden}: access to the {relation.ToLowerInvariant()} tenant is forbidden " +
            "for this pipeline. Access needs the relation and a right of the node's identity in that tenant.",
            reasonCode);
    }

    /// <summary>
    ///     A static configuration error of the node (no data involved, so the precise text reveals nothing about other
    ///     tenants).
    /// </summary>
    internal static CrossTenantAccessDeniedException InvalidConfiguration(string nodePath, string problem)
    {
        return new CrossTenantAccessDeniedException(
            $"[{nodePath}]: {CrossTenantAccessCodes.Forbidden}: invalid target tenant configuration: {problem}",
            CrossTenantRefusalReason.InvalidTargetConfiguration);
    }
}
