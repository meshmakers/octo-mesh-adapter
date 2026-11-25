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
    /// <summary>
    ///     Relation of the target to the pipeline's tenant. Must be named: the pipeline serializer does not enforce
    ///     <c>required</c>, so a missing relation stays <c>null</c> and the guard refuses it as a configuration error
    ///     instead of defaulting to <see cref="TenantRelation.Parent" /> (AB#9768).
    /// </summary>
    public required TenantRelation? Relation { get; init; }

    /// <summary>
    ///     Id of the target tenant (<see cref="TenantRelation.Sibling" /> and <see cref="TenantRelation.Child" />).
    ///     Not allowed for <see cref="TenantRelation.Parent" />, and not together with <see cref="TenantIdPath" />.
    /// </summary>
    public string? TenantId { get; init; }

    /// <summary>
    ///     JSON path to the id of the target tenant in the data context, instead of <see cref="TenantId" />. Only for a
    ///     node that runs as a verified caller: with <c>ServiceAccount</c>, <c>System</c>, or <c>Caller</c> without a
    ///     verified caller (it falls back to the service account) the access is refused at run time, and the
    ///     communication controller refuses such a deploy for <c>ServiceAccount</c> and <c>System</c> (AB#9767).
    /// </summary>
    public string? TenantIdPath { get; init; }
}

/// <summary>
///     Fan-out setting of ApplyChanges@2 (AB#9770): the node writes its batch as a one-way master-data sync into several
///     tenants of the pipeline's subtree. Every target goes through the cross-tenant guard exactly like a single
///     <see cref="TenantTargetConfiguration" />.
/// </summary>
/// <remarks>
///     v1 allows only <see cref="TenantRelation.Child" />: the master data of a group tenant flows down into its
///     companies. Without <see cref="TenantIds" /> the targets are all registered direct children of the pipeline's
///     tenant at execution time; with it, exactly the listed tenants, each of which must be a direct child.
/// </remarks>
public record TenantSetConfiguration
{
    /// <summary>
    ///     Relation of the targets to the pipeline's tenant. Must be named; only <see cref="TenantRelation.Child" /> is
    ///     accepted. A missing value stays <c>null</c> and is refused as a configuration error.
    /// </summary>
    public required TenantRelation? Relation { get; init; }

    /// <summary>
    ///     The target tenants. <c>null</c> (default) = every registered direct child of the pipeline's tenant at
    ///     execution time; an empty list is a configuration error.
    /// </summary>
    public ICollection<string>? TenantIds { get; init; }
}

/// <summary>
///     What a multi-tenant read (<see cref="TenantReadSetConfiguration" />) does with a target tenant the cross-tenant
///     guard refuses (AB#9771).
/// </summary>
public enum CrossTenantRefusalHandling
{
    /// <summary>
    ///     Default. One refused tenant fails the execution with <c>CROSS_TENANT_FORBIDDEN</c>, naming every refused
    ///     tenant; nothing is read, not even from the tenants that were allowed.
    /// </summary>
    Fail,

    /// <summary>
    ///     The allowed tenants are read; every refused tenant is listed in the node's output (<c>RefusedTenants</c>) and
    ///     none of its data is returned.
    /// </summary>
    Report
}

/// <summary>
///     Multi-tenant setting of the read nodes GetRtEntitiesByType@1 and GetRtEntitiesById@1 (AB#9771): the node reads
///     the same selection from several tenants of the pipeline's subtree, for example a group tenant consolidating the
///     figures of its companies. Every target goes through the cross-tenant guard exactly like a single
///     <see cref="TenantTargetConfiguration" />.
/// </summary>
/// <remarks>
///     Same shape and rules as the fan-out of ApplyChanges@2 (<see cref="TenantSetConfiguration" />): v1 allows only
///     <see cref="TenantRelation.Child" />; without <see cref="TenantSetConfiguration.TenantIds" /> the targets are all
///     registered direct children of the pipeline's tenant at execution time.
/// </remarks>
public record TenantReadSetConfiguration : TenantSetConfiguration
{
    /// <summary>
    ///     What happens when the guard refuses a target: <see cref="CrossTenantRefusalHandling.Fail" /> (default) or
    ///     <see cref="CrossTenantRefusalHandling.Report" />. Nullable because a key written without a value overwrites an
    ///     initializer in the pipeline YAML; <c>null</c> means <see cref="CrossTenantRefusalHandling.Fail" />.
    /// </summary>
    public CrossTenantRefusalHandling? OnRefused { get; init; }
}
