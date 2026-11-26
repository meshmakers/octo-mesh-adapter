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

    /// <summary>
    ///     The target tenant comes from data (<c>tenantIdPath</c>) but the node does not run as a verified caller: a
    ///     service account (configured, or the fallback of a <c>Caller</c> node without a verified caller) or the system
    ///     identity may name a target tenant only statically. AB#9767.
    /// </summary>
    public const string DataDrivenTargetNeedsCaller = "data_driven_target_needs_caller";

    /// <summary>
    ///     A cross-tenant write would associate an entity of the target with an entity the target does not hold (for
    ///     example an RtId of the pipeline's own tenant). AB#9769.
    /// </summary>
    public const string CrossTenantAssociation = "cross_tenant_association";
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
    ///     The refused tenants of a multi-tenant read with <c>onRefused: Fail</c> (AB#9771), in target order; empty for
    ///     the refusal of a single target.
    /// </summary>
    public IReadOnlyList<string> RefusedTenantIds { get; private init; } = [];

    /// <summary>
    ///     A multi-tenant read with <c>onRefused: Fail</c> (AB#9771) was refused for at least one of its targets, so it
    ///     read nothing. Names every refused tenant (the targets are static configuration or the registry's children,
    ///     never data); the per-target reason stays in <see cref="ReasonCode" /> of the first refusal and in the log.
    /// </summary>
    internal static CrossTenantAccessDeniedException ForbiddenTenants(string nodePath, int targetCount,
        IReadOnlyList<CrossTenantAccessDeniedException> refusals, IReadOnlyList<string> refusedTenantIds)
    {
        var names = string.Join(", ", refusedTenantIds.Select(t => $"'{t}'"));
        return new CrossTenantAccessDeniedException(
            $"[{nodePath}]: {CrossTenantAccessCodes.Forbidden}: access to {refusedTenantIds.Count} of {targetCount} " +
            $"target tenants is forbidden for this pipeline: {names}. Nothing was read (onRefused: Fail). Access " +
            "needs the relation and a right of the node's identity in every tenant.",
            refusals[0].ReasonCode)
        {
            RefusedTenantIds = refusedTenantIds
        };
    }

    /// <summary>
    ///     The uniform refusal: the same text whatever the reason, so the answer does not tell whether a tenant exists,
    ///     lies outside the relation or holds no right for the identity. It names the relation the node configured,
    ///     never the relation the target actually has: "the sibling tenant" would claim a relation that a tenant
    ///     outside the subtree does not have, and the actual one would tell what the uniform text hides.
    /// </summary>
    internal static CrossTenantAccessDeniedException Forbidden(string nodePath, string relation, string reasonCode)
    {
        return new CrossTenantAccessDeniedException(
            $"[{nodePath}]: {CrossTenantAccessCodes.Forbidden}: access to the target tenant is forbidden for this " +
            $"pipeline. Access needs the configured relation ({relation.ToLowerInvariant()}) to the pipeline's tenant " +
            "and a right of the node's identity in the target tenant.",
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
