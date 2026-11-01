using System.Collections;
using System.Text.RegularExpressions;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
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
///         <item><b>Write</b>: <see cref="EnsureWritesAllowedAsync(IMeshEtlContext, ICkCacheService, IEnumerable{IEntityUpdateInfo{RtEntity}}, IEnumerable{AssociationUpdateInfo})" /> refuses Hidden always, ReadOnly always, MethodOnly
///         unless the run is the method run of the entity's type (marker present, method belongs to the type, marker
///         pipeline is the running pipeline - the pattern of <see cref="GenericCreateGuard" />), and a Replace of
///         an entity whose type has an attribute the run may not write (the replacement would clear it).</item>
///         <item><b>Identity-owned</b> (AB#8059): the identity-owned attributes of <c>System.Identity/User</c> and the edges
///         of the identity association roles (<see cref="IdentityOwnedWrites" />, the engine's one list, also enforced by
///         asset-repo GraphQL and the user-started RT import) are refused like a <c>MethodOnly</c> attribute: allowed only
///         in the method run of a <c>System.Identity</c> type (for an attribute: the entity's type; for an association:
///         a type at either end), on insert, update, replace and association insert and delete.</item>
///     </list>
///     A type that is not in the tenant CK cache is not judged (nothing is known about it). An unloaded cache is loaded
///     first; if that fails the guard fails closed.
/// </remarks>
public static class RtAttributeAccessGuard
{
    private const int MaxDepth = 16;

    // An entity selector `[key=value]` inside a path (AB#8049); group 1 is the key.
    private static readonly Regex EntitySelectorRegex = new(@"\[([^\[\]=]+)=[^\[\]]*\]", RegexOptions.Compiled);

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
    /// <remarks>
    ///     An entity-selector key (<c>nav.Type[passwordHash=X]-&gt;name</c>) is an equality lookup on the key, so a key
    ///     that names a Hidden attribute of any CK type of the tenant is refused as well (AB#8049). The error message
    ///     shows the path with the selector values removed.
    /// </remarks>
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

            var hiddenSelectorKey = FindHiddenSelectorKey(ckCacheService, tenantId, path);
            if (hiddenSelectorKey != null)
            {
                throw new RtAttributeHiddenException(node, WithoutSelectorValues(path), hiddenSelectorKey, usage);
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
                throw new RtAttributeHiddenException(node, WithoutSelectorValues(path), hidden, usage);
            }
        }
    }

    /// <summary>
    ///     The Hidden attribute an entity-selector key of the path names (<c>[key=value]</c>), checked by its trailing
    ///     attribute name against every CK type of the tenant (conservative), or <c>null</c>.
    /// </summary>
    private static string? FindHiddenSelectorKey(ICkCacheService ckCacheService, string tenantId, string path)
    {
        if (!path.Contains('['))
        {
            return null;
        }

        foreach (Match match in EntitySelectorRegex.Matches(path))
        {
            var key = match.Groups[1].Value.Trim();
            var lastDot = key.LastIndexOf('.');
            var name = lastDot >= 0 ? key[(lastDot + 1)..] : key;
            if (name.Length == 0)
            {
                continue;
            }

            // The key may be a record path (Credentials.Token): check it as written and by its trailing attribute.
            var hidden = ckCacheService.GetCkTypes(tenantId)
                .SelectMany(t => new[] { key, name }.Select(candidate =>
                    ArchiveHiddenColumnGuard.FindHiddenAttribute(ckCacheService, tenantId, t.CkTypeId.ToRtCkId(),
                        candidate)))
                .FirstOrDefault(h => h != null);
            if (hidden != null)
            {
                return hidden;
            }
        }

        return null;
    }

    /// <summary>
    ///     The path with the values of its entity selectors removed (<c>[key=…]</c>), for error messages: everything from
    ///     the first <c>=</c> inside a selector up to the last <c>]</c> of the path is replaced, so a value that itself
    ///     contains <c>]</c> cannot leak a part of it.
    /// </summary>
    internal static string WithoutSelectorValues(string path)
    {
        var open = path.IndexOf('[');
        var equals = open < 0 ? -1 : path.IndexOf('=', open);
        var close = path.LastIndexOf(']');
        return equals < 0 || close < equals ? path : path[..equals] + "=…" + path[close..];
    }

    // ------------------------------------------------------------ stored query

    /// <summary>
    ///     Refuses a stored query (<c>System/PersistentQuery</c> and its subtypes) whose own columns, field filters, sort
    ///     orders, attribute-search paths, group-by columns or aggregation columns reach a Hidden attribute (AB#8049).
    ///     The asset repository refuses to store such a query, but a query can also arrive through an import, a restore
    ///     or an older version, so every node that runs a stored query checks it before it reaches the repository: a
    ///     stored filter on a hidden attribute is an oracle for its value (the number of rows answers the guess). The
    ///     paths are checked against the query type and every type derived from it; a query type the CK cache does
    ///     not know (e.g. a CK interface) is checked against every type of the tenant (fail closed). The caller has
    ///     made sure the cache is loaded (<see cref="EnsureCacheLoadedAsync" />).
    /// </summary>
    /// <exception cref="RtAttributeHiddenException">A stored path reaches a hidden attribute</exception>
    public static void EnsureStoredQueryNotHidden(ICkCacheService ckCacheService, string tenantId, string node,
        RtPersistentQuery query)
    {
        ArgumentNullException.ThrowIfNull(ckCacheService);
        ArgumentNullException.ThrowIfNull(query);
        var types = QueryTargetTypes(ckCacheService, tenantId, query.QueryCkTypeId);
        foreach (var (usage, paths) in StoredQueryPaths(query))
        {
            EnsureNotHidden(ckCacheService, tenantId, node, types, paths, usage);
        }
    }

    /// <summary>
    ///     Refuses a path (e.g. a field filter of the node configuration) that reaches a Hidden attribute of the query type
    ///     or of any type derived from it - the set of types a stored query returns; an unknown query type is checked
    ///     against every type of the tenant (fail closed). Same type set as <see cref="EnsureStoredQueryNotHidden" />.
    /// </summary>
    /// <exception cref="RtAttributeHiddenException">A path reaches a hidden attribute</exception>
    public static void EnsureNotHiddenForQueryType(ICkCacheService ckCacheService, string tenantId, string node,
        string? queryCkTypeId, IEnumerable<string?> paths, string usage)
    {
        ArgumentNullException.ThrowIfNull(ckCacheService);
        EnsureNotHidden(ckCacheService, tenantId, node, QueryTargetTypes(ckCacheService, tenantId, queryCkTypeId), paths,
            usage);
    }

    /// <summary>Every path a stored query uses, grouped by what it is used for.</summary>
    internal static IEnumerable<(string Usage, IEnumerable<string?> Paths)> StoredQueryPaths(RtPersistentQuery query)
    {
        switch (query)
        {
            case RtSimpleRtQuery simple:
                yield return ("stored query column", Strings(simple.Columns));
                yield return ("stored field filter", Paths(simple.FieldFilter));
                yield return ("stored sort order", Paths(simple.Sorting));
                yield return ("stored attribute search", Strings(simple.AttributeSearchFilter?.AttributePaths));
                break;
            case RtAggregationRtQuery aggregation:
                yield return ("stored aggregation column", Paths(aggregation.Columns));
                yield return ("stored field filter", Paths(aggregation.FieldFilter));
                yield return ("stored attribute search", Strings(aggregation.AttributeSearchFilter?.AttributePaths));
                break;
            case RtGroupingAggregationRtQuery grouping:
                yield return ("stored group-by column", Strings(grouping.GroupingColumns));
                yield return ("stored aggregation column", Paths(grouping.Columns));
                yield return ("stored field filter", Paths(grouping.FieldFilter));
                yield return ("stored attribute search", Strings(grouping.AttributeSearchFilter?.AttributePaths));
                break;
            case RtStreamDataQuery streamData:
                yield return ("stored field filter", Paths(streamData.FieldFilter));
                switch (streamData)
                {
                    case RtSimpleSdQuery simpleSd:
                        yield return ("stored query column", Strings(simpleSd.Columns));
                        yield return ("stored sort order", Paths(simpleSd.Sorting));
                        break;
                    case RtAggregationSdQuery aggregationSd:
                        yield return ("stored aggregation column", Paths(aggregationSd.Columns));
                        break;
                    case RtGroupingAggregationSdQuery groupingSd:
                        yield return ("stored group-by column", Strings(groupingSd.GroupingColumns));
                        yield return ("stored aggregation column", Paths(groupingSd.Columns));
                        break;
                    case RtDownsamplingSdQuery downsamplingSd:
                        yield return ("stored aggregation column", Paths(downsamplingSd.Columns));
                        break;
                }

                break;
        }
    }

    private static IEnumerable<string?> Strings(IEnumerable<string>? paths) => paths ?? Enumerable.Empty<string>();

    private static IEnumerable<string?> Paths(IEnumerable<RtFieldFilterRecord>? filters) =>
        filters?.Select(f => f.AttributePath) ?? Enumerable.Empty<string?>();

    private static IEnumerable<string?> Paths(IEnumerable<RtSortOrderItemRecord>? sortOrders) =>
        sortOrders?.Select(s => s.AttributePath) ?? Enumerable.Empty<string?>();

    private static IEnumerable<string?> Paths(IEnumerable<RtAggregationQueryColumnRecord>? columns) =>
        columns?.Select(c => c.AttributePath) ?? Enumerable.Empty<string?>();

    /// <summary>
    ///     The query type and every type derived from it; every type of the tenant when the cache does not know the
    ///     query type (fail closed).
    /// </summary>
    private static IReadOnlyList<RtCkId<CkTypeId>> QueryTargetTypes(ICkCacheService ckCacheService, string tenantId,
        string? queryCkTypeId)
    {
        if (!string.IsNullOrWhiteSpace(queryCkTypeId) &&
            ckCacheService.TryGetRtCkType(tenantId, new RtCkId<CkTypeId>(queryCkTypeId), out var graph) &&
            graph != null)
        {
            return graph.GetAllDerivedTypes(true).Select(t => t.ToRtCkId()).Prepend(graph.CkTypeId.ToRtCkId())
                .Distinct().ToList();
        }

        return ckCacheService.GetCkTypes(tenantId).Select(t => t.CkTypeId.ToRtCkId()).ToList();
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
    public static Task EnsureWritesAllowedAsync(IMeshEtlContext etlContext, ICkCacheService ckCacheService,
        IEnumerable<IEntityUpdateInfo<RtEntity>> updateInfos) =>
        EnsureWritesAllowedAsync(etlContext, ckCacheService, updateInfos, []);

    /// <summary>
    ///     Runs the checks of a write batch with association changes: the replaced-create guard first (AB#7462), then the
    ///     attribute access of every value the batch writes and the identity-owned attributes and associations (AB#8059).
    ///     Call it before the dry-run branch and before the first write; the whole batch is refused.
    /// </summary>
    public static async Task EnsureWritesAllowedAsync(IMeshEtlContext etlContext, ICkCacheService ckCacheService,
        IEnumerable<IEntityUpdateInfo<RtEntity>> updateInfos, IEnumerable<AssociationUpdateInfo> associationUpdates)
    {
        ArgumentNullException.ThrowIfNull(etlContext);
        ArgumentNullException.ThrowIfNull(ckCacheService);
        ArgumentNullException.ThrowIfNull(updateInfos);
        ArgumentNullException.ThrowIfNull(associationUpdates);

        var all = updateInfos.ToList();

        // AB#7462 first: an insert of a type whose method replaces the generic create.
        await GenericCreateGuard.EnsureAllowedAsync(etlContext, ckCacheService, all);

        var updates = all.Where(u => u.ModOption != EntityModOptions.Delete).ToList();
        var identityAssociations = associationUpdates
            .Where(a => IdentityOwnedWrites.IsIdentityOwnedAssociation(a.RoleId)).ToList();
        if (updates.Count == 0 && identityAssociations.Count == 0)
        {
            return;
        }

        await EnsureCacheLoadedAsync(etlContext, ckCacheService);

        List<RtAttributeWriteRefusal>? refusals = null;

        // AB#8059: an identity association edge (insert or delete, either direction) only in the method run of a
        // System.Identity type at one of its ends. The message names the role and the two types, never an rtId or a value.
        foreach (var association in identityAssociations)
        {
            if (IsIdentityMethodRunFor(etlContext, ckCacheService, association.Origin.CkTypeId) ||
                IsIdentityMethodRunFor(etlContext, ckCacheService, association.Target.CkTypeId))
            {
                continue;
            }

            (refusals ??= []).Add(new RtAttributeWriteRefusal(association.Origin.CkTypeId.FullName,
                $"association {association.RoleId.FullName} -> {association.Target.CkTypeId.FullName}",
                RtAttributeWriteRefusalCause.AssociationAccess, RtAttributeWriteRefusalCause.IdentityOwned));
        }

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

            var reported = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (path, attribute) in written)
            {
                var cause = RefusalCause(attribute.Access, methodCause);
                // AB#8059: the identity list judges the top-level attributes of the User type (a record member path such
                // as Claims[0].Value is covered by its attribute, which is collected as a top-level path as well).
                if (cause == null && IsTopLevel(path) &&
                    IdentityOwnedWrites.IsIdentityOwnedAttribute(update.CkTypeId, attribute.AttributeName))
                {
                    cause = IdentityOwnedCause(methodCause);
                }

                if (cause != null && reported.Add(path))
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
                    var mayNotWrite = RefusalCause(attribute.Access, methodCause) != null ||
                                      (IdentityOwnedWrites.IsIdentityOwnedAttribute(update.CkTypeId,
                                           attribute.AttributeName) && IdentityOwnedCause(methodCause) != null);
                    if (mayNotWrite && !writtenTop.Contains(attribute.AttributeName))
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
    ///     AB#8059: the cause token for a write of an identity-owned User attribute, null in the method run of the User type
    ///     (the same rule as <c>MethodOnly</c>: the list stands in for the CK access until the next System.Identity major).
    /// </summary>
    private static string? IdentityOwnedCause(string? methodRunCause) =>
        methodRunCause == null ? null : RtAttributeWriteRefusalCause.IdentityOwned;

    private static bool IsTopLevel(string path) => path.IndexOfAny(['.', '[']) < 0;

    /// <summary>
    ///     AB#8059: true when this run is the method run of <paramref name="ckTypeId" /> (marker present, method of the type,
    ///     running pipeline) and the method is declared by a <c>System.Identity</c> type. A type that is not in the cache
    ///     never qualifies.
    /// </summary>
    private static bool IsIdentityMethodRunFor(IMeshEtlContext etlContext, ICkCacheService ckCacheService,
        RtCkId<CkTypeId> ckTypeId)
    {
        var marker = etlContext.MethodInvocation;
        if (marker == null ||
            !ckCacheService.TryGetRtCkType(etlContext.TenantId, ckTypeId, out var graph) || graph == null ||
            GetMethodRunCause(etlContext, graph) != null)
        {
            return false;
        }

        var method = graph.AllMethods.Values.First(m =>
            string.Equals(m.QualifiedMethodId, marker.MethodId, StringComparison.Ordinal));
        // CkModelId.Name is the model name without the version.
        return string.Equals(method.DeclaringCkTypeId.ModelId.Name, IdentityModelName, StringComparison.OrdinalIgnoreCase);
    }

    private const string IdentityModelName = "System.Identity";

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
