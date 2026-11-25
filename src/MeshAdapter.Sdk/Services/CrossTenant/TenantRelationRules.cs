using Meshmakers.Octo.MeshAdapter.Nodes.Common;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>
///     The platform tenant registry at one point in time: every registered tenant with its registry parent. Tenant ids
///     are trimmed and compared case-insensitively.
/// </summary>
public sealed class TenantRegistrySnapshot
{
    private readonly Dictionary<string, string?> _parents;

    /// <param name="systemTenantId">Id of the system tenant (the root of the installation).</param>
    /// <param name="tenants">Registered tenants with their parent; a missing parent means the system tenant.</param>
    public TenantRegistrySnapshot(string systemTenantId, IEnumerable<(string TenantId, string? ParentTenantId)> tenants)
    {
        SystemTenantId = Normalize(systemTenantId)
                         ?? throw new ArgumentException("The system tenant id is required.", nameof(systemTenantId));
        _parents = new Dictionary<string, string?>(StringComparer.OrdinalIgnoreCase);
        foreach (var (tenantId, parentTenantId) in tenants)
        {
            var id = Normalize(tenantId);
            if (id == null)
            {
                continue;
            }

            // A row without a parent hangs below the system tenant (the registry stamps children of the system tenant
            // either way).
            _parents[id] = Normalize(parentTenantId) ?? SystemTenantId;
        }
    }

    /// <summary>The system tenant id.</summary>
    public string SystemTenantId { get; }

    /// <summary>Whether <paramref name="tenantId" /> is registered.</summary>
    public bool IsRegistered(string tenantId) => _parents.ContainsKey(tenantId);

    /// <summary>
    ///     The registered direct children of <paramref name="tenantId" />, ordered by id. Empty for the system tenant (its
    ///     children are top-level tenants, never sync targets) and for an unregistered tenant.
    /// </summary>
    public IReadOnlyList<string> ChildrenOf(string tenantId)
    {
        if (IsSystemTenant(tenantId) || !IsRegistered(tenantId))
        {
            return [];
        }

        return _parents.Where(p => p.Value != null && string.Equals(p.Value, tenantId, StringComparison.OrdinalIgnoreCase))
            .Select(p => p.Key)
            .OrderBy(id => id, StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>Whether <paramref name="tenantId" /> is the system tenant.</summary>
    public bool IsSystemTenant(string tenantId) =>
        string.Equals(tenantId, SystemTenantId, StringComparison.OrdinalIgnoreCase);

    /// <summary>Registry parent of a registered tenant, or <c>null</c> for the system tenant and unknown tenants.</summary>
    public string? ParentOf(string tenantId)
    {
        if (IsSystemTenant(tenantId))
        {
            return null;
        }

        return _parents.TryGetValue(tenantId, out var parent) ? parent : null;
    }

    /// <summary>Trims an id; blank → <c>null</c>.</summary>
    public static string? Normalize(string? tenantId) =>
        string.IsNullOrWhiteSpace(tenantId) ? null : tenantId.Trim();
}

/// <summary>
///     The relation rules of cross-tenant access, as a pure function of the registry. The only place that decides
///     whether a target is in a source's relation.
/// </summary>
/// <remarks>
///     Invariant: a target is allowed only when both source and target are registered, neither is the system tenant,
///     they differ, and the target is exactly the named relation (parent, a sibling under a non-system parent, or a
///     direct child). Everything else, including deeper descendants, ancestors above the parent and cousins, is
///     refused.
/// </remarks>
public static class TenantRelationRules
{
    /// <summary>
    ///     The target of <see cref="TenantRelation.Parent" /> for <paramref name="sourceTenantId" />: its registry
    ///     parent, or <c>null</c> when it has none that could be a target.
    /// </summary>
    public static string? ResolveParent(TenantRegistrySnapshot registry, string sourceTenantId) =>
        registry.ParentOf(sourceTenantId);

    /// <summary>
    ///     Checks whether <paramref name="targetTenantId" /> is in <paramref name="relation" /> of
    ///     <paramref name="sourceTenantId" />.
    /// </summary>
    /// <returns><c>null</c> when allowed, otherwise the <see cref="CrossTenantRefusalReason" /> token.</returns>
    public static string? Check(TenantRegistrySnapshot registry, string sourceTenantId, TenantRelation relation,
        string? targetTenantId)
    {
        if (string.IsNullOrWhiteSpace(targetTenantId))
        {
            // E.g. Parent of a top-level tenant (its parent is the system tenant, which ParentOf already hides) or of
            // a tenant that is not registered.
            return CrossTenantRefusalReason.RelationNotAllowed;
        }

        if (registry.IsSystemTenant(sourceTenantId) || registry.IsSystemTenant(targetTenantId))
        {
            return CrossTenantRefusalReason.RelationNotAllowed;
        }

        if (string.Equals(sourceTenantId, targetTenantId, StringComparison.OrdinalIgnoreCase))
        {
            // The own tenant is the default path of a node, never a cross-tenant target.
            return CrossTenantRefusalReason.RelationNotAllowed;
        }

        if (!registry.IsRegistered(targetTenantId))
        {
            return CrossTenantRefusalReason.TargetNotRegistered;
        }

        if (!registry.IsRegistered(sourceTenantId))
        {
            // A detached source has no place in the tree any more.
            return CrossTenantRefusalReason.RelationNotAllowed;
        }

        var sourceParent = registry.ParentOf(sourceTenantId);
        var targetParent = registry.ParentOf(targetTenantId);

        var allowed = relation switch
        {
            TenantRelation.Parent => Same(sourceParent, targetTenantId),
            // Two children of the system tenant are not siblings: that would open every top-level tenant to every other.
            TenantRelation.Sibling => sourceParent != null && !registry.IsSystemTenant(sourceParent) &&
                                      Same(sourceParent, targetParent),
            TenantRelation.Child => Same(targetParent, sourceTenantId),
            _ => false
        };

        return allowed ? null : CrossTenantRefusalReason.RelationNotAllowed;
    }

    private static bool Same(string? a, string? b) =>
        a != null && b != null && string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
}
