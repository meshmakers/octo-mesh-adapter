using System.Collections;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Runtime.Engine.StreamData;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Common;

/// <summary>
///     The one place that applies the CK v2 attribute access (<c>Hidden</c>, <c>MethodOnly</c>, <c>ReadOnly</c>) to
///     pipeline nodes (AB#7768, Feature AB#5696, building on AB#7767). A pipeline runs with system rights and reads the
///     repository directly, so the GraphQL guards do not reach it; without this a three-node pipeline could read a
///     password hash or write a method-guarded attribute.
/// </summary>
/// <remarks>
///     <list type="bullet">
///         <item><b>Read</b>: <see cref="DropHidden(ICkCacheService, string, RtEntity)" /> removes Hidden attributes
///         (also record members) from the entities an extract node emits. The repository's own, possibly cached,
///         entity is never modified: a changed entity is a copy.</item>
///         <item><b>Filter / sort / column</b>: <see cref="EnsureNotHidden" /> refuses a path that reaches a Hidden
///         attribute (an oracle for its value). The error names the node and the path, never a value.</item>
///         <item><b>Write</b>: <see cref="EnsureWritesAllowedAsync" /> refuses Hidden always, ReadOnly always, MethodOnly
///         unless the run is the method run of the entity's type (marker present, method belongs to the type, marker
///         pipeline is the running pipeline - the pattern of <see cref="GenericCreateGuard" />), and a Replace of
///         an entity whose type has an attribute the run may not write (the replacement would clear it).</item>
///     </list>
///     A type that is not in the tenant CK cache is not judged (nothing is known about it). An unloaded cache is loaded
///     first; if that fails the guard fails closed.
/// </remarks>
public static class RtAttributeAccessGuard
{
    private const int MaxDepth = 16;

    // ---------------------------------------------------------------- read

    /// <summary>
    ///     Loads the tenant CK cache when it is not loaded (the cache can be unloaded in the middle of a run); fails
    ///     closed with <see cref="RtAttributeAccessCheckUnavailableException" />.
    /// </summary>
    public static async Task EnsureCacheLoadedAsync(IMeshEtlContext etlContext, ICkCacheService ckCacheService)
    {
        ArgumentNullException.ThrowIfNull(etlContext);
        ArgumentNullException.ThrowIfNull(ckCacheService);
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
            throw new RtAttributeAccessCheckUnavailableException(etlContext.TenantId, e);
        }

        if (!ckCacheService.IsTenantLoaded(etlContext.TenantId))
        {
            throw new RtAttributeAccessCheckUnavailableException(etlContext.TenantId, null);
        }
    }

    /// <summary>
    ///     Returns the entity without its Hidden attributes. The same instance when nothing is hidden, otherwise a copy.
    ///     The caller has made sure the cache is loaded (<see cref="EnsureCacheLoadedAsync" />).
    /// </summary>
    public static RtEntity DropHidden(ICkCacheService ckCacheService, string tenantId, RtEntity entity)
    {
        ArgumentNullException.ThrowIfNull(ckCacheService);
        ArgumentNullException.ThrowIfNull(entity);
        if (entity.CkTypeId == null ||
            !ckCacheService.TryGetRtCkType(tenantId, entity.CkTypeId, out var graph) || graph == null)
        {
            return entity;
        }

        var attributes = ProjectAttributes(ckCacheService, tenantId, graph, entity.Attributes, 0, out var changed);
        if (!changed)
        {
            return entity;
        }

        return new RtEntity(entity.CkTypeId, entity.RtId, attributes)
        {
            RtCreationDateTime = entity.RtCreationDateTime,
            RtChangedDateTime = entity.RtChangedDateTime,
            RtArchivedDateTime = entity.RtArchivedDateTime,
            RtWellKnownName = entity.RtWellKnownName,
            RtCreatedBy = entity.RtCreatedBy,
            RtDisplayName = entity.RtDisplayName,
            RtDisplayDescription = entity.RtDisplayDescription,
            RtVersion = entity.RtVersion,
            RtState = entity.RtState
        };
    }

    /// <summary>Loads the cache if needed and drops the Hidden attributes of every entity of the result.</summary>
    public static async Task<IResultSet<RtEntity>> DropHiddenAsync(IMeshEtlContext etlContext,
        ICkCacheService ckCacheService, IResultSet<RtEntity> resultSet)
    {
        ArgumentNullException.ThrowIfNull(resultSet);
        await EnsureCacheLoadedAsync(etlContext, ckCacheService);
        var items = resultSet.Items.ToList();
        var projected = items.Select(e => DropHidden(ckCacheService, etlContext.TenantId, e)).ToList();
        if (projected.Where((e, i) => !ReferenceEquals(e, items[i])).Any() == false)
        {
            return resultSet;
        }

        return new ResultSet<RtEntity>(projected, resultSet.TotalCount, resultSet.AggregationResult,
            resultSet.FieldAggregationResult);
    }

    private static Dictionary<string, object?> ProjectAttributes(ICkCacheService cache, string tenantId,
        CkTypeWithAttributesGraph graph, IReadOnlyDictionary<string, object?> source, int depth, out bool changed)
    {
        changed = false;
        var result = new Dictionary<string, object?>(source.Count);
        foreach (var (name, value) in source)
        {
            var attribute = SecretAttributes.FindAttribute(graph, name);
            if (attribute is { IsReadableOutsideMethod: false })
            {
                changed = true;
                continue;
            }

            var projected = value;
            if (attribute != null && depth < MaxDepth)
            {
                projected = ProjectValue(cache, tenantId, value, depth, ref changed);
            }

            result[name] = projected;
        }

        return result;
    }

    private static object? ProjectValue(ICkCacheService cache, string tenantId, object? value, int depth,
        ref bool changed)
    {
        switch (value)
        {
            case RtRecord record:
                return ProjectRecord(cache, tenantId, record, depth, ref changed);
            case IEnumerable items and not string and not IDictionary:
                List<object?>? copy = null;
                var index = 0;
                foreach (var item in items)
                {
                    var projected = item is RtRecord r ? ProjectRecord(cache, tenantId, r, depth, ref changed) : item;
                    if (!ReferenceEquals(projected, item))
                    {
                        copy ??= items.Cast<object?>().ToList();
                        copy[index] = projected;
                    }

                    index++;
                }

                return copy ?? value;
            default:
                return value;
        }
    }

    private static RtRecord ProjectRecord(ICkCacheService cache, string tenantId, RtRecord record, int depth,
        ref bool changed)
    {
        if (record.CkRecordId == null ||
            !cache.TryGetRtCkRecord(tenantId, record.CkRecordId, out var recordGraph) || recordGraph == null)
        {
            return record;
        }

        var attributes = ProjectAttributes(cache, tenantId, recordGraph, record.Attributes, depth + 1,
            out var recordChanged);
        if (!recordChanged)
        {
            return record;
        }

        changed = true;
        return new RtRecord(record.CkRecordId, attributes);
    }

    // ---------------------------------------------------------- filter / sort

    /// <summary>
    ///     Refuses a configured path (field filter, sort order, query column) that reaches a Hidden attribute of any of
    ///     the given types. A path that navigates through associations (<c>-&gt;</c>, <c>::</c>) is checked by its
    ///     trailing attribute against every CK type of the tenant (conservative: refused if any type hides it).
    /// </summary>
    /// <exception cref="RtAttributeHiddenException">A path reaches a hidden attribute</exception>
    public static void EnsureNotHidden(ICkCacheService ckCacheService, string tenantId, string node,
        IEnumerable<RtCkId<CkTypeId>> ckTypeIds, IEnumerable<string?> paths, string usage)
    {
        ArgumentNullException.ThrowIfNull(ckCacheService);
        var types = ckTypeIds.ToList();
        foreach (var path in paths)
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                continue;
            }

            string? hidden = null;
            var navigationIndex = path.LastIndexOf("->", StringComparison.Ordinal);
            if (navigationIndex >= 0 || path.Contains("::", StringComparison.Ordinal))
            {
                var tail = navigationIndex >= 0 ? path[(navigationIndex + 2)..] : string.Empty;
                if (tail.Length > 0 && !tail.Contains("::", StringComparison.Ordinal))
                {
                    hidden = ckCacheService.GetCkTypes(tenantId)
                        .Select(t => ArchiveHiddenColumnGuard.FindHiddenAttribute(ckCacheService, tenantId,
                            t.CkTypeId.ToRtCkId(), tail))
                        .FirstOrDefault(h => h != null);
                }
            }
            else
            {
                hidden = types
                    .Select(t => ArchiveHiddenColumnGuard.FindHiddenAttribute(ckCacheService, tenantId, t, path))
                    .FirstOrDefault(h => h != null);
            }

            if (hidden != null)
            {
                throw new RtAttributeHiddenException(node, path, hidden, usage);
            }
        }
    }

    /// <summary>
    ///     The CK types behind a CK interface (all implementing types), for filters and sort orders of an interface query.
    /// </summary>
    public static IReadOnlyList<RtCkId<CkTypeId>> TypesOfInterface(ICkCacheService ckCacheService, string tenantId,
        RtCkId<CkInterfaceId> ckInterfaceId)
    {
        try
        {
            var graph = ckCacheService.GetRtCkInterface(tenantId, ckInterfaceId);
            return graph.ImplementingTypes.Select(t => t.ToRtCkId()).ToList();
        }
        catch (Exception e) when (e is not OutOfMemoryException)
        {
            // Unknown interface: the engine refuses the query itself.
            return [];
        }
    }

    // ----------------------------------------------------------------- write

    /// <summary>
    ///     Runs the checks of a write batch: the replaced-create guard first (AB#7462), then the attribute access of
    ///     every value the batch writes. Call it before the dry-run branch and before the first write; the whole batch is
    ///     refused.
    /// </summary>
    public static async Task EnsureWritesAllowedAsync(IMeshEtlContext etlContext, ICkCacheService ckCacheService,
        IEnumerable<IEntityUpdateInfo<RtEntity>> updateInfos)
    {
        ArgumentNullException.ThrowIfNull(etlContext);
        ArgumentNullException.ThrowIfNull(ckCacheService);
        ArgumentNullException.ThrowIfNull(updateInfos);

        var all = updateInfos.ToList();

        // AB#7462 first: an insert of a type whose method replaces the generic create.
        await GenericCreateGuard.EnsureAllowedAsync(etlContext, ckCacheService, all);

        var updates = all.Where(u => u.ModOption != EntityModOptions.Delete).ToList();
        if (updates.Count == 0)
        {
            return;
        }

        await EnsureCacheLoadedAsync(etlContext, ckCacheService);

        List<RtAttributeWriteRefusal>? refusals = null;
        foreach (var update in updates)
        {
            if (!ckCacheService.TryGetRtCkType(etlContext.TenantId, update.CkTypeId, out var graph) || graph == null)
            {
                continue;
            }

            var typeName = update.CkTypeId.FullName;
            void Report(string path, CkAttributeAccessDto access, string cause) =>
                (refusals ??= []).Add(new RtAttributeWriteRefusal(typeName, path, access.ToString(), cause));

            var methodCause = GetMethodRunCause(etlContext, graph);

            // Names written by this update (top-level attributes, record members, array item updates, secret clears).
            var written = new List<(string Path, CkTypeAttributeGraph Attribute)>();
            if (update.RtEntity != null)
            {
                CollectWritten(ckCacheService, etlContext.TenantId, graph, update.RtEntity.Attributes, string.Empty,
                    0, written);
            }

            foreach (var item in update.RecordArrayItemUpdates ?? [])
            {
                var attribute = SecretAttributes.FindAttribute(graph, item.AttributeName);
                if (attribute == null)
                {
                    continue;
                }

                written.Add((attribute.AttributeName, attribute));
                if (attribute.ValueCkRecordId != null &&
                    ckCacheService.TryGetRtCkRecord(etlContext.TenantId, attribute.ValueCkRecordId.ToRtCkId(),
                        out var recordGraph) && recordGraph != null)
                {
                    foreach (var member in item.MemberUpdates.Keys.Append(item.MatchMember))
                    {
                        var memberAttribute = SecretAttributes.FindAttribute(recordGraph, member);
                        if (memberAttribute != null)
                        {
                            written.Add(($"{attribute.AttributeName}.{memberAttribute.AttributeName}",
                                memberAttribute));
                        }
                    }
                }
            }

            foreach (var name in update.ClearSecretAttributes ?? [])
            {
                var attribute = SecretAttributes.FindAttribute(graph, name);
                if (attribute != null)
                {
                    written.Add((attribute.AttributeName, attribute));
                }
            }

            foreach (var (path, attribute) in written)
            {
                var cause = RefusalCause(attribute.Access, methodCause);
                if (cause != null)
                {
                    Report(path, attribute.Access, cause);
                }
            }

            if (update.ModOption == EntityModOptions.Replace)
            {
                // A replacement document that omits an attribute the run may not write clears the stored value.
                var writtenTop = written.Select(w => w.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
                foreach (var attribute in graph.AllAttributes.Values)
                {
                    if (RefusalCause(attribute.Access, methodCause) != null &&
                        !writtenTop.Contains(attribute.AttributeName))
                    {
                        Report(attribute.AttributeName, attribute.Access, RtAttributeWriteRefusalCause.ReplaceWouldClear);
                    }
                }
            }
        }

        if (refusals != null)
        {
            throw new RtAttributeWriteRestrictedException(refusals);
        }
    }

    /// <summary>The cause token for a write of an attribute of this access, null when the write is allowed.</summary>
    private static string? RefusalCause(CkAttributeAccessDto access, string? methodRunCause) => access switch
    {
        CkAttributeAccessDto.Hidden => RtAttributeWriteRefusalCause.Hidden,
        CkAttributeAccessDto.ReadOnly => RtAttributeWriteRefusalCause.ReadOnly,
        CkAttributeAccessDto.MethodOnly => methodRunCause,
        _ => null
    };

    /// <summary>
    ///     Null when this run is the method run of the entity's type: the marker names a method of the type (own or
    ///     inherited) and belongs to the running pipeline. Otherwise the MethodOnly cause token.
    /// </summary>
    private static string? GetMethodRunCause(IMeshEtlContext etlContext, CkTypeGraph graph)
    {
        var marker = etlContext.MethodInvocation;
        if (marker == null)
        {
            return RtAttributeWriteRefusalCause.MethodOnlyMissingMarker;
        }

        if (!graph.AllMethods.Values.Any(m => string.Equals(m.QualifiedMethodId, marker.MethodId,
                StringComparison.Ordinal)))
        {
            return RtAttributeWriteRefusalCause.MethodOnlyMethodMismatch;
        }

        if (!string.Equals(marker.PipelineRtId, etlContext.PipelineRtEntityId.RtId.ToString(),
                StringComparison.OrdinalIgnoreCase))
        {
            return RtAttributeWriteRefusalCause.MethodOnlyPipelineMismatch;
        }

        return null;
    }

    private static void CollectWritten(ICkCacheService cache, string tenantId, CkTypeWithAttributesGraph graph,
        IReadOnlyDictionary<string, object?> values, string prefix, int depth,
        List<(string Path, CkTypeAttributeGraph Attribute)> written)
    {
        foreach (var (name, value) in values)
        {
            var attribute = SecretAttributes.FindAttribute(graph, name);
            if (attribute == null)
            {
                continue;
            }

            var path = prefix + attribute.AttributeName;
            written.Add((path, attribute));
            if (depth >= MaxDepth)
            {
                continue;
            }

            switch (value)
            {
                case RtRecord record:
                    CollectRecord(cache, tenantId, record, path, depth, written);
                    break;
                case IEnumerable items and not string and not IDictionary:
                    var index = 0;
                    foreach (var item in items)
                    {
                        if (item is RtRecord listRecord)
                        {
                            CollectRecord(cache, tenantId, listRecord, $"{path}[{index}]", depth, written);
                        }

                        index++;
                    }

                    break;
            }
        }
    }

    private static void CollectRecord(ICkCacheService cache, string tenantId, RtRecord record, string path, int depth,
        List<(string Path, CkTypeAttributeGraph Attribute)> written)
    {
        if (record.CkRecordId != null &&
            cache.TryGetRtCkRecord(tenantId, record.CkRecordId, out var recordGraph) && recordGraph != null)
        {
            CollectWritten(cache, tenantId, recordGraph, record.Attributes, path + ".", depth + 1, written);
        }
    }
}
