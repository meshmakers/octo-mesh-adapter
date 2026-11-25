using System.Collections.Concurrent;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.Services;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>
///     Access to one other tenant, granted by <see cref="ICrossTenantAccessGuard" />. What a node uses instead of
///     <see cref="IMeshEtlContext.TenantRepository" /> and <see cref="IMeshEtlContext.GetSessionForAsync" /> when it has a
///     target-tenant setting.
/// </summary>
/// <remarks>
///     🔴 Sessions come only from <see cref="OpenSessionAsync" />, which uses the context proven for
///     <see cref="TenantId" />. A node never calls <c>Repository.GetSessionAsync(...)</c> itself: there is no system or
///     source-tenant session for another tenant. Attribute guards and CK lookups of the node use
///     <see cref="TenantId" />, never the pipeline's tenant.
/// </remarks>
public interface ICrossTenantAccess
{
    /// <summary>The target tenant (normalised).</summary>
    string TenantId { get; }

    /// <summary>The target tenant's repository; its CK cache is loaded. Query only with sessions of <see cref="OpenSessionAsync" />.</summary>
    ITenantRepository Repository { get; }

    /// <summary>A session in the target tenant under the identity proven for it.</summary>
    Task<IOctoSession> OpenSessionAsync();
}

/// <summary>Everything the guard needs from one execution. Holds the caller's token; never logged.</summary>
public sealed class CrossTenantAccessRequest
{
    /// <summary>Creates the request of one node call; the properties below describe the parameters.</summary>
    public CrossTenantAccessRequest(string sourceTenantId, TenantTargetConfiguration target,
        NodeExecutionIdentity identity, IDataContext dataContext, string nodePath,
        VerifiedPrincipal? caller, string? callerAccessToken, IPipelineIdentityResolver? identityResolver,
        IGlobalConfiguration? globalConfiguration, CrossTenantProofCache proofCache)
    {
        SourceTenantId = sourceTenantId;
        Target = target;
        Identity = identity;
        DataContext = dataContext;
        NodePath = nodePath;
        Caller = caller;
        CallerAccessToken = callerAccessToken;
        IdentityResolver = identityResolver;
        GlobalConfiguration = globalConfiguration;
        ProofCache = proofCache;
    }

    /// <summary>The tenant of the pipeline's registration (never the adapter's own tenant).</summary>
    public string SourceTenantId { get; }

    /// <summary>The node's target-tenant setting.</summary>
    public TenantTargetConfiguration Target { get; }

    /// <summary>The node's configured identity.</summary>
    public NodeExecutionIdentity Identity { get; }

    /// <summary>The data context, for <see cref="TenantTargetConfiguration.TenantIdPath" />.</summary>
    public IDataContext DataContext { get; }

    /// <summary>The node path, for messages.</summary>
    public string NodePath { get; }

    /// <summary>The trigger-verified caller of the execution, if any.</summary>
    public VerifiedPrincipal? Caller { get; }

    /// <summary>The caller's access token. Credential material.</summary>
    public string? CallerAccessToken { get; }

    /// <summary>The execution's identity resolver (service account of the pipeline).</summary>
    public IPipelineIdentityResolver? IdentityResolver { get; }

    /// <summary>The pipeline's configuration.</summary>
    public IGlobalConfiguration? GlobalConfiguration { get; }

    /// <summary>Proofs of this execution.</summary>
    public CrossTenantProofCache ProofCache { get; }

    /// <inheritdoc />
    public override string ToString() => $"cross-tenant access {SourceTenantId} -> {Target.Relation} ({Identity}) at {NodePath}";
}

/// <summary>
///     Proofs of one execution, keyed by target tenant and subject kind. Lives on the execution's context and dies with
///     it, so no proof is reused by another execution, caller or pipeline.
/// </summary>
public sealed class CrossTenantProofCache
{
    private readonly ConcurrentDictionary<(string TenantId, CrossTenantSubjectKind Kind), CrossTenantProof> _proofs =
        new();

    internal bool TryGet(string tenantId, CrossTenantSubjectKind kind, out CrossTenantProof proof) =>
        _proofs.TryGetValue((tenantId.ToLowerInvariant(), kind), out proof!);

    internal void Set(string tenantId, CrossTenantSubjectKind kind, CrossTenantProof proof) =>
        _proofs[(tenantId.ToLowerInvariant(), kind)] = proof;
}

/// <summary>
///     The one door to another tenant's data. The only adapter code that opens another tenant's repository
///     (<c>CrossTenantCallSiteArchitectureTests</c>).
/// </summary>
/// <remarks>
///     Invariant: a pipeline of tenant T reaches tenant U ≠ T only when U is in the relation the node names (from the
///     tenant registry) and the identity service proved a right of the executing identity in U (tenant and generation of
///     the proof checked). The system identity is never accepted for U. Every refusal is a
///     <see cref="CrossTenantAccessDeniedException" /> before any data of U is read or written.
/// </remarks>
public interface ICrossTenantAccessGuard
{
    /// <summary>Checks the access and opens the target tenant, or throws <see cref="CrossTenantAccessDeniedException" />.</summary>
    Task<ICrossTenantAccess> OpenAsync(CrossTenantAccessRequest request, CancellationToken cancellationToken = default);

    /// <summary>
    ///     The registered direct children of <paramref name="sourceTenantId" /> (AB#9770, fan-out of the master-data
    ///     sync). Only a list of candidates: every one of them is still opened with <see cref="OpenAsync" />, which checks
    ///     relation and identity again. Empty for the system tenant and an unregistered source; throws
    ///     <see cref="CrossTenantAccessDeniedException" /> when the registry cannot be read (fail closed).
    /// </summary>
    Task<IReadOnlyList<string>> ListChildTenantsAsync(string sourceTenantId, string nodePath,
        CancellationToken cancellationToken = default);
}
