namespace Meshmakers.Octo.MeshAdapter.Nodes.Common;

/// <summary>
///     How the target tenant of a cross-tenant node relates to the tenant the pipeline is registered in. Evaluated on
///     the platform tenant registry at execution time, never on identity providers or data.
/// </summary>
/// <remarks>
///     There is deliberately no "any descendant" value: a node reaches at most one level up, its siblings or one level
///     down. The system tenant is never a valid target or source, and two children of the system tenant are not
///     siblings.
/// </remarks>
public enum TenantRelation
{
    /// <summary>The registry parent of the pipeline's tenant. Takes no tenant id.</summary>
    Parent,

    /// <summary>Another registered child of the same (non-system) parent. Needs a tenant id.</summary>
    Sibling,

    /// <summary>A registered direct child of the pipeline's tenant. Needs a tenant id.</summary>
    Child
}

/// <summary>
///     Target-tenant setting of a data node that may reach another tenant of the pipeline's subtree. Without it (the
///     default, <c>null</c>) a node works on its own tenant exactly as before.
/// </summary>
/// <remarks>
///     The relation is always static configuration, so data can never widen it; only the tenant id of a
///     <see cref="TenantRelation.Sibling" /> or <see cref="TenantRelation.Child" /> target may come from the data context
///     (<see cref="TenantIdPath" />), and such an id is checked exactly like a configured one. Access additionally needs
///     a right of the executing identity in the target tenant, proven by the identity service; the system identity is
///     always refused.
/// </remarks>
public record TenantTargetConfiguration
{
    /// <summary>Relation of the target to the pipeline's tenant.</summary>
    public required TenantRelation Relation { get; init; }

    /// <summary>
    ///     Id of the target tenant (<see cref="TenantRelation.Sibling" /> and <see cref="TenantRelation.Child" />).
    ///     Not allowed for <see cref="TenantRelation.Parent" />, and not together with <see cref="TenantIdPath" />.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    ///     JSON path to the id of the target tenant in the data context, instead of <see cref="TenantId" />.
    /// </summary>
    public string? TenantIdPath { get; init; }
}
