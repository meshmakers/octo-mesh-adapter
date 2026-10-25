using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     Shared pre-check of the nodes that insert entities (ApplyChanges@1/@2, ImportFromExcel; wired in AB#7463):
///     an insert of a type whose CK method declares <c>replacesGenericCreate</c> (own or inherited) is allowed only
///     inside that method's own pipeline run (AB#7462, Feature AB#6884 F3.7, decision note AB#7120).
/// </summary>
/// <remarks>
///     <para>
///         An insert of a flagged type passes only if <b>all three</b> hold: the run carries a
///         <see cref="IMeshEtlContext.MethodInvocation" /> marker, its <c>MethodId</c> equals the qualified id of the
///         replacing method (ordinal) and its <c>PipelineRtId</c> equals the id of the running pipeline. Otherwise the
///         <b>whole batch</b> is refused with <see cref="GenericCreateReplacedByMethodException" /> (code
///         <see cref="GenericCreateReplacement.ReasonToken" />) before the caller writes anything.
///     </para>
///     <para>
///         Only <see cref="EntityModOptions.Insert" /> is checked: Update, Replace and Delete cannot create an entity
///         of that type. Types that are not in the tenant CK cache are not refused here (the create fails on its
///         own). The lookup is memoized per type per call, so a batch of 10 000 inserts costs one cache lookup per
///         distinct type.
///     </para>
///     <para>
///         Call it before the dry-run branch and before the first write.
///     </para>
/// </remarks>
public static class GenericCreateGuard
{
    /// <summary>
    ///     Throws <see cref="GenericCreateReplacedByMethodException" /> when the batch contains a not allowed insert.
    /// </summary>
    /// <param name="etlContext">The run context (marker, running pipeline, tenant)</param>
    /// <param name="ckCacheService">The tenant CK cache</param>
    /// <param name="updateInfos">The entity update infos that are about to be applied</param>
    /// <exception cref="GenericCreateReplacedByMethodException">The batch is refused as a whole</exception>
    /// <exception cref="GenericCreateCheckUnavailableException">
    ///     The tenant CK cache is not loaded and could not be loaded; the check cannot be made, so the batch is refused
    ///     (fail closed)
    /// </exception>
    public static async Task EnsureAllowedAsync(IMeshEtlContext etlContext, ICkCacheService ckCacheService,
        IEnumerable<IEntityUpdateInfo<RtEntity>> updateInfos)
    {
        ArgumentNullException.ThrowIfNull(etlContext);
        ArgumentNullException.ThrowIfNull(ckCacheService);
        ArgumentNullException.ThrowIfNull(updateInfos);

        // Materialized once: the enumerable is walked twice (is there an insert at all, then the check).
        var inserts = updateInfos.Where(u => u.ModOption == EntityModOptions.Insert).ToList();
        if (inserts.Count == 0)
        {
            return;
        }

        await EnsureTenantCacheLoadedAsync(etlContext, ckCacheService);

        // type -> qualified id of the replacing method, null when the generic create is allowed
        var replacingMethodByType = new Dictionary<RtCkId<CkTypeId>, string?>();
        List<GenericCreateRefusal>? refusals = null;

        foreach (var updateInfo in inserts)
        {
            var ckTypeId = updateInfo.CkTypeId;
            if (!replacingMethodByType.TryGetValue(ckTypeId, out var replacingMethodId))
            {
                replacingMethodId =
                    GenericCreateReplacement.TryGetReplacingMethod(ckCacheService, etlContext.TenantId, ckTypeId,
                        out var method)
                        ? method.QualifiedMethodId
                        : null;
                replacingMethodByType[ckTypeId] = replacingMethodId;
            }

            if (replacingMethodId == null)
            {
                continue;
            }

            var cause = GetRefusalCause(etlContext, replacingMethodId);
            if (cause != null)
            {
                (refusals ??= []).Add(new GenericCreateRefusal(ckTypeId.FullName, replacingMethodId, cause));
            }
        }

        if (refusals != null)
        {
            throw new GenericCreateReplacedByMethodException(refusals);
        }
    }

    /// <summary>
    ///     <c>TryGetReplacingMethod</c> answers "no replacing method" for a tenant whose cache is not loaded, which would
    ///     let the insert through while the repository reloads the cache on its own and writes. The cache can be unloaded
    ///     in the middle of a run (lease leave, CkModelChanged), so the guard loads it first and fails closed when that
    ///     does not work.
    /// </summary>
    private static async Task EnsureTenantCacheLoadedAsync(IMeshEtlContext etlContext, ICkCacheService ckCacheService)
    {
        if (ckCacheService.IsTenantLoaded(etlContext.TenantId))
        {
            return;
        }

        try
        {
            await etlContext.TenantRepository.LoadCacheForTenantAsync(ckCacheService);
        }
        catch (Exception e) when (e is not OperationCanceledException)
        {
            throw new GenericCreateCheckUnavailableException(etlContext.TenantId, e);
        }

        if (!ckCacheService.IsTenantLoaded(etlContext.TenantId))
        {
            throw new GenericCreateCheckUnavailableException(etlContext.TenantId, null);
        }
    }

    private static string? GetRefusalCause(IMeshEtlContext etlContext, string replacingMethodId)
    {
        var marker = etlContext.MethodInvocation;
        if (marker == null)
        {
            return GenericCreateRefusalCause.MissingMarker;
        }

        if (!string.Equals(marker.MethodId, replacingMethodId, StringComparison.Ordinal))
        {
            return GenericCreateRefusalCause.MethodMismatch;
        }

        // The pipeline id is an ObjectId hex string; compare it the way ids are compared, not as a secret.
        if (!string.Equals(marker.PipelineRtId, etlContext.PipelineRtEntityId.RtId.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return GenericCreateRefusalCause.PipelineMismatch;
        }

        return null;
    }
}
