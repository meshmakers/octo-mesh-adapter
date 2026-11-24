using System.Diagnostics.Metrics;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

/// <summary>
///     Default <see cref="ICrossTenantAccessGuard" />. See <see cref="ICrossTenantAccessGuard" /> for the invariant and
///     the documentation page on cross-tenant access for the abuse cases each step answers.
/// </summary>
/// <remarks>
///     Order of the checks (each one refuses before the next runs, and none reads data of the target):
///     <list type="number">
///         <item>static configuration (relation and id combination) — abuse case "Parent with an id";</item>
///         <item>node identity <c>System</c> — refused, no exception;</item>
///         <item>target from configuration or data, then the relation on the registry — outside the subtree, detached,
///         system tenant, ancestors above the parent, cousins, grandchildren, data-driven ids;</item>
///         <item>effective identity: the user caller, else the service account; a fallback to the system context is
///         refused;</item>
///         <item>proof by the identity service; its tenant must be the target and its context not the system context
///         (a proof for another tenant is never applied);</item>
///         <item>generation of the proof against the target's current generation — recreated tenant ids;</item>
///         <item>only then the target's repository and CK cache.</item>
///     </list>
///     Unreadable registry, identity service, generation store or repository refuse (fail closed).
/// </remarks>
internal sealed class CrossTenantAccessGuard : ICrossTenantAccessGuard
{
    internal const string MeterName = "Meshmakers.Octo.MeshAdapter.CrossTenant";
    private static readonly Meter Meter = new(MeterName);

    private static readonly Counter<long> Decisions =
        Meter.CreateCounter<long>("octo.mesh_adapter.cross_tenant.decisions", description:
            "Cross-tenant access decisions of the guard");

    private readonly ISystemContext _systemContext;
    private readonly ICkCacheService _ckCacheService;
    private readonly ITenantRegistryReader _registryReader;
    private readonly ICrossTenantIdentityProvider _identityProvider;
    private readonly ITenantGenerationReader? _generationReader;
    private readonly ILogger<CrossTenantAccessGuard> _logger;

    public CrossTenantAccessGuard(ISystemContext systemContext, ICkCacheService ckCacheService,
        ITenantRegistryReader registryReader, ICrossTenantIdentityProvider identityProvider,
        ILogger<CrossTenantAccessGuard> logger, ITenantGenerationReader? generationReader = null)
    {
        _systemContext = systemContext;
        _ckCacheService = ckCacheService;
        _registryReader = registryReader;
        _identityProvider = identityProvider;
        _logger = logger;
        _generationReader = generationReader;
    }

    public async Task<ICrossTenantAccess> OpenAsync(CrossTenantAccessRequest request,
        CancellationToken cancellationToken = default)
    {
        var target = request.Target;
        var relation = target.Relation.ToString();
        var source = TenantRegistrySnapshot.Normalize(request.SourceTenantId) ?? string.Empty;
        string? resolvedTarget = null;

        Exception Refuse(string reason)
        {
            Record(request, source, resolvedTarget, reason);
            return CrossTenantAccessDeniedException.Forbidden(request.NodePath, relation, reason);
        }

        // 1. Static configuration.
        var configurationProblem = CheckConfiguration(target);
        if (configurationProblem != null)
        {
            Record(request, source, null, CrossTenantRefusalReason.InvalidTargetConfiguration);
            throw CrossTenantAccessDeniedException.InvalidConfiguration(request.NodePath, configurationProblem);
        }

        // 2. The system identity never reaches another tenant.
        if (request.Identity == NodeExecutionIdentity.System)
        {
            throw Refuse(CrossTenantRefusalReason.SystemIdentityRefused);
        }

        // 3. Target and relation.
        TenantRegistrySnapshot registry;
        try
        {
            registry = await _registryReader.GetAsync(cancellationToken: cancellationToken);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{SourceTenantId}] Tenant registry unreadable for a cross-tenant access", source);
            throw Refuse(CrossTenantRefusalReason.TargetUnverifiable);
        }

        if (target.Relation == TenantRelation.Parent)
        {
            resolvedTarget = TenantRelationRules.ResolveParent(registry, source);
        }
        else if (!TryResolveTargetId(request, out resolvedTarget))
        {
            throw Refuse(CrossTenantRefusalReason.InvalidTargetConfiguration);
        }

        var relationRefusal = TenantRelationRules.Check(registry, source, target.Relation, resolvedTarget);
        if (relationRefusal == CrossTenantRefusalReason.TargetNotRegistered)
        {
            // A child created seconds ago is not in the cached snapshot yet: read the registry once more.
            try
            {
                registry = await _registryReader.GetAsync(refresh: true, cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "[{SourceTenantId}] Tenant registry unreadable for a cross-tenant access", source);
                throw Refuse(CrossTenantRefusalReason.TargetUnverifiable);
            }

            relationRefusal = TenantRelationRules.Check(registry, source, target.Relation, resolvedTarget);
        }

        if (relationRefusal != null)
        {
            throw Refuse(relationRefusal);
        }

        var targetTenantId = resolvedTarget!;

        // 4. Effective identity — never the system context.
        CrossTenantSubjectKind kind;
        RtSecurityContext sourceIdentity;
        if (request.Identity == NodeExecutionIdentity.Caller && request.Caller != null)
        {
            // A verified caller is proven with the caller's own token. Without one there is nothing to exchange, and
            // falling back to the service account would elevate the node beyond the caller's rights.
            kind = CrossTenantSubjectKind.User;
            sourceIdentity = RtSecurityContext.ForUser(request.Caller.SubjectId, request.Caller.Roles);
            if (string.IsNullOrWhiteSpace(request.CallerAccessToken))
            {
                throw Refuse(CrossTenantRefusalReason.IdentityNotProvable);
            }
        }
        else
        {
            // ServiceAccount, or Caller without a caller (schedule, watch, event): the pipeline's service account. The
            // resolver answers the system context when none is configured — that fallback stops here.
            kind = CrossTenantSubjectKind.ServiceAccount;
            if (request.IdentityResolver == null)
            {
                throw Refuse(CrossTenantRefusalReason.SystemIdentityRefused);
            }

            try
            {
                sourceIdentity = await request.IdentityResolver.ResolveServiceAccountAsync(cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex,
                    "[{SourceTenantId}] Service account unavailable for a cross-tenant access", source);
                throw Refuse(CrossTenantRefusalReason.IdentityNotProvable);
            }

            if (sourceIdentity.IsSystem)
            {
                throw Refuse(CrossTenantRefusalReason.SystemIdentityRefused);
            }
        }

        // 5. Proof by the identity service, memoised for this execution only.
        if (!request.ProofCache.TryGet(targetTenantId, kind, out var proof))
        {
            CrossTenantProof? proven;
            try
            {
                proven = await _identityProvider.ProveAsync(new CrossTenantProofRequest(source, targetTenantId, kind,
                    sourceIdentity, kind == CrossTenantSubjectKind.User ? request.CallerAccessToken : null,
                    request.GlobalConfiguration), cancellationToken);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // The message of the provider names no credential; the exception itself is not passed on.
                _logger.LogWarning("[{SourceTenantId}] Identity proof for '{TargetTenantId}' unavailable: {Reason}",
                    source, targetTenantId, ex.Message);
                throw Refuse(CrossTenantRefusalReason.TargetUnverifiable);
            }

            if (proven == null)
            {
                throw Refuse(CrossTenantRefusalReason.IdentityNotProvable);
            }

            proof = proven;
        }

        // A proof is applied only to the tenant it was issued for, and never as the system context.
        if (!string.Equals(TenantRegistrySnapshot.Normalize(proof.TenantId), targetTenantId,
                StringComparison.OrdinalIgnoreCase) || proof.SecurityContext.IsSystem)
        {
            throw Refuse(CrossTenantRefusalReason.IdentityNotProvable);
        }

        // 6. Generation: a proof for an earlier tenant with the same id is worthless.
        var generationRefusal = await CheckGenerationAsync(targetTenantId, proof.Generation, cancellationToken);
        if (generationRefusal != null)
        {
            throw Refuse(generationRefusal);
        }

        request.ProofCache.Set(targetTenantId, kind, proof);

        // 7. Only now the target's repository — the one call site of the adapter that opens another tenant.
        ITenantRepository repository;
        try
        {
            repository = await _systemContext.FindTenantRepositoryAsync(targetTenantId);
            await repository.LoadCacheForTenantAsync(_ckCacheService);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "[{SourceTenantId}] Repository of '{TargetTenantId}' could not be opened",
                source, targetTenantId);
            throw Refuse(CrossTenantRefusalReason.TargetUnverifiable);
        }

        Record(request, source, targetTenantId, null);
        return new CrossTenantAccess(targetTenantId, repository, proof.SecurityContext);
    }

    /// <summary>The static rules of the setting; <c>null</c> when consistent.</summary>
    internal static string? CheckConfiguration(TenantTargetConfiguration target)
    {
        var hasId = !string.IsNullOrWhiteSpace(target.TenantId);
        var hasPath = !string.IsNullOrWhiteSpace(target.TenantIdPath);
        return target.Relation switch
        {
            TenantRelation.Parent when hasId || hasPath => "relation Parent takes no tenantId or tenantIdPath",
            TenantRelation.Sibling or TenantRelation.Child when hasId && hasPath =>
                "set either tenantId or tenantIdPath, not both",
            TenantRelation.Sibling or TenantRelation.Child when !hasId && !hasPath =>
                $"relation {target.Relation} needs tenantId or tenantIdPath",
            TenantRelation.Parent or TenantRelation.Sibling or TenantRelation.Child => null,
            _ => $"unknown relation '{target.Relation}'"
        };
    }

    private static bool TryResolveTargetId(CrossTenantAccessRequest request, out string? tenantId)
    {
        if (!string.IsNullOrWhiteSpace(request.Target.TenantId))
        {
            tenantId = TenantRegistrySnapshot.Normalize(request.Target.TenantId);
            return tenantId != null;
        }

        try
        {
            tenantId = TenantRegistrySnapshot.Normalize(request.DataContext.Get<string>(request.Target.TenantIdPath!));
        }
        catch (Exception)
        {
            // Not a string, or a path that cannot be read: the data does not name a usable tenant.
            tenantId = null;
        }

        return tenantId != null;
    }

    private async Task<string?> CheckGenerationAsync(string tenantId, string? proofGeneration,
        CancellationToken cancellationToken)
    {
        if (_generationReader == null || string.IsNullOrWhiteSpace(proofGeneration))
        {
            // Without a generation on both sides a recreated tenant cannot be told apart: refuse.
            return CrossTenantRefusalReason.TargetUnverifiable;
        }

        try
        {
            var current = await _generationReader.GetGenerationAsync(tenantId, cancellationToken: cancellationToken);
            if (current != null && string.Equals(current, proofGeneration, StringComparison.Ordinal))
            {
                return null;
            }

            // The cached generation may be older than the token: read it once more before refusing.
            current = await _generationReader.GetGenerationAsync(tenantId, refresh: true, cancellationToken);
            if (current == null)
            {
                return CrossTenantRefusalReason.TargetUnverifiable;
            }

            return string.Equals(current, proofGeneration, StringComparison.Ordinal)
                ? null
                : CrossTenantRefusalReason.TenantGenerationMismatch;
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            _logger.LogWarning(ex, "Generation of tenant '{TargetTenantId}' unreadable", tenantId);
            return CrossTenantRefusalReason.TargetUnverifiable;
        }
    }

    private void Record(CrossTenantAccessRequest request, string source, string? target, string? reason)
    {
        var outcome = reason == null ? "allowed" : "refused";
        var configuredTarget = request.Target.TenantId ?? request.Target.TenantIdPath ?? "(parent)";
        if (reason == null)
        {
            _logger.LogInformation(
                "[{SourceTenantId}] Cross-tenant access {Outcome}: {Relation} '{TargetTenantId}' at {NodePath} as {Identity}",
                source, outcome, request.Target.Relation, target, request.NodePath, request.Identity);
        }
        else
        {
            _logger.LogWarning(
                "[{SourceTenantId}] Cross-tenant access {Outcome} ({Reason}): {Relation} '{TargetTenantId}' (configured '{ConfiguredTarget}') at {NodePath} as {Identity}",
                source, outcome, reason, request.Target.Relation, target ?? "(unresolved)", configuredTarget,
                request.NodePath, request.Identity);
        }

        Decisions.Add(1,
            new KeyValuePair<string, object?>("source", source),
            new KeyValuePair<string, object?>("target", target ?? "(unresolved)"),
            new KeyValuePair<string, object?>("relation", request.Target.Relation.ToString()),
            new KeyValuePair<string, object?>("mode", request.Identity.ToString()),
            new KeyValuePair<string, object?>("outcome", outcome),
            new KeyValuePair<string, object?>("reason", reason ?? "none"));
    }

    private sealed class CrossTenantAccess(string tenantId, ITenantRepository repository,
        RtSecurityContext securityContext) : ICrossTenantAccess
    {
        public string TenantId { get; } = tenantId;

        public ITenantRepository Repository { get; } = repository;

        public Task<IOctoSession> OpenSessionAsync() => Repository.GetSessionAsync(securityContext);
    }
}
