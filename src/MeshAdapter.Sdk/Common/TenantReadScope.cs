using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     The tenant a read node reads from (AB#9768): its own tenant, or — with a <see cref="TenantTargetConfiguration" />
///     — another tenant of the subtree opened by the cross-tenant guard.
/// </summary>
/// <remarks>
///     Invariants:
///     <list type="bullet">
///         <item>Without a target the scope is exactly the node's former path: <c>etlContext.TenantRepository</c> and
///         <c>etlContext.TenantId</c>; the node opens its own session with <c>etlContext.GetSessionForAsync(identity)</c>
///         at the same point as before.</item>
///         <item>With a target, repository, tenant id and session come only from
///         <see cref="IMeshEtlContext.OpenTenantAsync" /> (the guard): the session runs under the identity proven for
///         the target, never the source identity or the system context. A refusal throws before any read.</item>
///         <item>Hidden attributes are checked and dropped with the CK model of the tenant the data comes from.</item>
///     </list>
/// </remarks>
internal sealed class TenantReadScope
{
    private readonly IMeshEtlContext? _ownContext;
    private readonly ICrossTenantAccess? _crossTenant;

    private TenantReadScope(IMeshEtlContext ownContext)
    {
        _ownContext = ownContext;
        TenantId = ownContext.TenantId;
        Repository = ownContext.TenantRepository;
    }

    private TenantReadScope(ICrossTenantAccess access)
    {
        _crossTenant = access;
        TenantId = access.TenantId;
        Repository = access.Repository;
    }

    /// <summary>The tenant the data is read from.</summary>
    public string TenantId { get; }

    /// <summary>The repository of <see cref="TenantId" />.</summary>
    public ITenantRepository Repository { get; }

    /// <summary>Whether the scope is another tenant than the pipeline's.</summary>
    public bool IsCrossTenant => _crossTenant != null;

    /// <summary>
    ///     Opens the scope. <paramref name="target" /> <c>null</c> = the own tenant (no call is made); otherwise the guard
    ///     decides, and a refusal (<c>CROSS_TENANT_FORBIDDEN</c>) fails the node.
    /// </summary>
    public static async Task<TenantReadScope> OpenAsync(IMeshEtlContext etlContext, TenantTargetConfiguration? target,
        NodeExecutionIdentity identity, IDataContext dataContext, INodeContext nodeContext)
    {
        if (target == null)
        {
            return new TenantReadScope(etlContext);
        }

        var access = await etlContext.OpenTenantAsync(target, identity, dataContext, nodeContext);
        return new TenantReadScope(access);
    }

    /// <summary>
    ///     The session of the target, under the identity the guard proved there. Only for a cross-tenant scope: the own
    ///     tenant's session stays at the node's own <c>GetSessionForAsync(identity)</c> call site, where the session
    ///     classification of AB#5028 sees it.
    /// </summary>
    public Task<IOctoSession> OpenTargetSessionAsync() =>
        _crossTenant?.OpenSessionAsync()
        ?? throw new InvalidOperationException("The own tenant's session is opened by the node itself.");

    /// <summary>Loads the CK cache of <see cref="TenantId" /> when needed (Hidden-attribute checks).</summary>
    public Task EnsureCacheLoadedAsync(ICkCacheService ckCacheService) =>
        _ownContext != null
            ? RtAttributeAccessGuard.EnsureCacheLoadedAsync(_ownContext, ckCacheService)
            : RtAttributeAccessGuard.EnsureCacheLoadedAsync(Repository, TenantId, ckCacheService);

    /// <summary>Drops the Hidden attributes of <see cref="TenantId" />'s CK model from every entity.</summary>
    public Task<IResultSet<RtEntity>> DropHiddenAsync(ICkCacheService ckCacheService, IResultSet<RtEntity> resultSet) =>
        _ownContext != null
            ? RtAttributeAccessGuard.DropHiddenAsync(_ownContext, ckCacheService, resultSet)
            : RtAttributeAccessGuard.DropHiddenAsync(Repository, TenantId, ckCacheService, resultSet);
}
