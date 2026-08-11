using System.Globalization;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes;

/// <summary>
/// Helpers shared by the nodes that read from a stream-data archive (<c>GetQueryById@1</c>,
/// <c>GetStreamData@1</c>, <c>AggregateStreamData@1</c>): UTC normalisation of configured time-range
/// boundaries, reading those boundaries from the pipeline data via JSONPath, column-name resolution,
/// and reading projected / aggregated values back out of a <c>StreamDataRow</c>.
/// <para>
/// Column names go through the storage layer's own field resolver rather than a derivation of the
/// attribute path. That is what makes computed columns work — theirs is versioned after a formula
/// change and cannot be derived (AB#4764) — and it means "does this column exist" is answered by the
/// same code the query would use.
/// </para>
/// </summary>
internal static class StreamDataNodeHelpers
{
    /// <summary>
    /// Physical CrateDB column carrying the start of a windowed archive's row window. Windowed
    /// archives (time-range / rollup) alias <c>window_end</c> as <c>timestamp</c> on read, so the
    /// window start has to be projected explicitly to reach <c>StreamDataRow.Values</c>.
    /// </summary>
    internal const string WindowStartColumn = "window_start";

    /// <summary>
    /// Physical CrateDB column carrying the end of a windowed archive's row window.
    /// </summary>
    internal const string WindowEndColumn = "window_end";

    /// <summary>
    /// Physical CrateDB column carrying the source entity's well-known name. A standard column on
    /// every archive table and registered case-insensitively by the storage layer's field resolver,
    /// so it can be used as an ordinary field filter.
    /// </summary>
    internal const string WellKnownNameColumn = "rtWellKnownName";

    /// <summary>
    /// Physical CrateDB column carrying a raw archive's timestamp. Windowed archives have no such
    /// column — their time axis is <c>window_end</c>.
    /// </summary>
    internal const string TimestampColumn = "timestamp";

    /// <summary>
    /// Translates a column name a caller may sort or filter by into the physical column the storage
    /// layer resolves, and rejects anything that would silently do nothing.
    /// <para>
    /// Two reasons this is needed. First, the result headers a node hands out (<c>Timestamp</c>,
    /// <c>WindowStart</c>, <c>WindowEnd</c>, <c>WellKnownName</c>) are not the physical names — the
    /// storage layer's resolver only knows <c>window_start</c> and friends, and its lookup is
    /// case-insensitive but not separator-insensitive, so <c>WindowStart</c> misses. Second, an
    /// unresolvable name is dropped without a trace by the storage layer (<c>AddSortOrders</c> and
    /// <c>BuildFieldFilterDtos</c> both <c>continue</c> past it) — a mistyped sort quietly returns
    /// unordered rows, a mistyped filter quietly returns too many.
    /// </para>
    /// </summary>
    internal static ResolvedColumn ResolveQueryableColumn(string name, ArchiveSnapshot snapshot,
        StreamDataFieldResolver resolver, INodeContext nodeContext, string usage)
    {
        var queryName = TranslateResultHeader(name, snapshot);

        // The storage layer's own resolver decides both whether the name exists and what key the row
        // values carry. Asking it rather than deriving the key is what makes computed columns work:
        // after a formula change their physical column is versioned ({base}__v{N}) and no derivation
        // from the attribute path can reproduce it (AB#4764). It also keeps the node honest about
        // which names are resolvable at all — the same judgement the query would apply.
        var resolved = resolver.Resolve(queryName);
        if (resolved == null)
        {
            throw MeshAdapterPipelineExecutionException.UnknownStreamDataColumn(
                nodeContext, name, usage, BuildKnownColumnList(snapshot));
        }

        return new ResolvedColumn(queryName, resolved.CrateDbName);
    }

    /// <summary>
    /// Translates the result-header vocabulary a node hands out into the physical names the storage
    /// resolver knows. Its lookup is case-insensitive but not separator-insensitive, so
    /// <c>WindowStart</c> would miss <c>window_start</c>; and a windowed archive has no
    /// <c>timestamp</c> column at all, its time axis being <c>window_end</c>. Anything else is passed
    /// through for the resolver to judge.
    /// </summary>
    private static string TranslateResultHeader(string name, ArchiveSnapshot snapshot)
    {
        var windowed = snapshot.UsesWindowedStorage;

        if (Matches(name, "Timestamp"))
        {
            return windowed ? WindowEndColumn : TimestampColumn;
        }

        // Only a windowed archive has a row window. On a raw archive these names are passed through
        // and the resolver rejects them, which is what should happen — that shape has no window.
        if (windowed && Matches(name, "WindowStart")) return WindowStartColumn;
        if (windowed && Matches(name, "WindowEnd")) return WindowEndColumn;

        if (Matches(name, "WellKnownName")) return WellKnownNameColumn;

        return name;
    }

    /// <summary>
    /// A column as both sides need it: the name to hand to the query, and the key
    /// <c>StreamDataRow.Values</c> carries the value under. The two differ for every dotted or
    /// mixed-case attribute path, and for a computed column they differ unpredictably.
    /// </summary>
    internal readonly record struct ResolvedColumn(string QueryName, string StorageKey);

    /// <summary>
    /// Builds the field resolver for an archive. One per node execution — it walks the archive's
    /// columns, so resolving each name against a fresh instance would be wasteful.
    /// <para>
    /// A null snapshot yields a resolver that knows only the standard columns. Callers that require
    /// the archive to exist reject a missing snapshot before getting here; the persisted-query node
    /// tolerates it because its downsampling path degrades rather than failing.
    /// </para>
    /// </summary>
    internal static StreamDataFieldResolver CreateFieldResolver(ArchiveSnapshot? snapshot)
        => snapshot != null
            ? StreamDataFieldResolver.CreateForArchive(snapshot)
            : new StreamDataFieldResolver();

    /// <summary>
    /// The archive's own data columns, as the resolver sees them: every column whose name it accepts.
    /// Computed columns mid-backfill are absent because the resolver does not register them — the read
    /// path deliberately hides them until their backfill commits.
    /// </summary>
    internal static List<ResolvedColumn> ResolveArchiveColumns(ArchiveSnapshot snapshot,
        StreamDataFieldResolver resolver)
    {
        var result = new List<ResolvedColumn>();

        foreach (var spec in snapshot.Columns)
        {
            // Ingested columns are addressed by path, computed ones by name.
            var name = !string.IsNullOrWhiteSpace(spec.Path) ? spec.Path : spec.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                continue;
            }

            var resolved = resolver.Resolve(name);
            if (resolved != null)
            {
                result.Add(new ResolvedColumn(name, resolved.CrateDbName));
            }
        }

        return result;
    }

    private static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The names a caller may use, for the error message: the node's result headers first (what a
    /// reader of the output would reach for), then the archive's own columns.
    /// </summary>
    private static string BuildKnownColumnList(ArchiveSnapshot snapshot)
    {
        var names = new List<string> { "Timestamp" };
        if (snapshot.UsesWindowedStorage)
        {
            names.Add("WindowStart");
            names.Add("WindowEnd");
        }

        names.Add("WellKnownName");
        names.AddRange(snapshot.Columns
            .Select(spec => !string.IsNullOrWhiteSpace(spec.Path) ? spec.Path : spec.Name)
            .Where(n => !string.IsNullOrWhiteSpace(n))
            .Select(n => n!));
        names.AddRange(Constants.GetDefaultStreamDataFields(snapshot.UsesWindowedStorage));

        return string.Join(", ", names.Distinct(StringComparer.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Normalises a resolved boundary to UTC. The stream-data nodes' <c>From</c>/<c>To</c> contract
    /// is UTC, so a value without a zone (JSON such as <c>"2026-07-01T00:00:00"</c>, which STJ
    /// surfaces as <see cref="DateTimeKind.Unspecified"/>) is read as UTC rather than being shifted
    /// by the server's local time zone. AB#4734.
    /// </summary>
    internal static DateTime ToUtc(DateTime value)
    {
        return value.Kind switch
        {
            DateTimeKind.Utc => value,
            DateTimeKind.Local => value.ToUniversalTime(),
            _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
        };
    }

    /// <summary>
    /// <see cref="ToUtc" /> for an optional boundary.
    /// </summary>
    internal static DateTime? ToUtcOrNull(DateTime? value)
    {
        return value.HasValue ? ToUtc(value.Value) : null;
    }

    /// <summary>
    /// Reads a time-range boundary from the data context. <see cref="IDataContext.GetValue"/> already
    /// converts ISO-8601 strings to <see cref="DateTime"/> (and takes the first match for a multi-match
    /// JSONPath); the string arm below covers looser formats that ISO detection rejects. A path that
    /// resolves to nothing is not an error — the caller falls back, and <paramref name="fallbackHint"/>
    /// describes to what — but a present value that is not a date/time is, because silently widening
    /// the queried range would hide the misconfiguration.
    /// </summary>
    internal static DateTime? ResolveDateTimeFromPath(IDataContext dataContext, INodeContext nodeContext,
        string? path, string propertyName, string fallbackHint)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = dataContext.GetValue(path);
        switch (value)
        {
            case null:
                nodeContext.Warning($"{propertyName} '{path}' resolved to no value; {fallbackHint}");
                return null;
            case DateTime dateTime:
                return ToUtc(dateTime);
            case DateTimeOffset dateTimeOffset:
                return dateTimeOffset.UtcDateTime;
            case string text when DateTime.TryParse(text, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed):
                // AdjustToUniversal already yields a UTC-kind value.
                return parsed;
            default:
                throw MeshAdapterPipelineExecutionException.InvalidDateTimeAtPath(nodeContext, path, value);
        }
    }

    /// <summary>
    /// Reads an integer from the data context. Same fallback contract as
    /// <see cref="ResolveDateTimeFromPath" />: an unresolved path warns, a present non-integer throws.
    /// </summary>
    internal static int? ResolveIntFromPath(IDataContext dataContext, INodeContext nodeContext,
        string? path, string propertyName, string fallbackHint)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var value = dataContext.GetValue(path);
        switch (value)
        {
            case null:
                nodeContext.Warning($"{propertyName} '{path}' resolved to no value; {fallbackHint}");
                return null;
            // Integers that fit in Int32 box to int, larger ones to long — see JsonScalar.ToClr.
            case int number:
                return number;
            case long number when number is >= int.MinValue and <= int.MaxValue:
                return (int)number;
            case double number when number % 1 == 0 && number is >= int.MinValue and <= int.MaxValue:
                return (int)number;
            case string text when int.TryParse(text, CultureInfo.InvariantCulture, out var parsed):
                return parsed;
            default:
                throw MeshAdapterPipelineExecutionException.InvalidIntegerAtPath(nodeContext, path, value);
        }
    }

    /// <summary>
    /// Reads a list of strings from the data context, accepting either a single scalar, a JSON array,
    /// or a multi-match JSONPath (wildcards / recursive descent). Null and blank entries are dropped.
    /// Returns <c>null</c> when the path resolves to nothing, so the caller can fall back.
    /// </summary>
    internal static IReadOnlyList<string>? ResolveStringListFromPath(IDataContext dataContext,
        INodeContext nodeContext, string? path, string propertyName, string fallbackHint)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return null;
        }

        var matches = dataContext.SelectMatches(path).ToList();
        var values = new List<string>();

        foreach (var match in matches)
        {
            if (match.GetKind("$") == DataKind.Array)
            {
                var length = match.Length("$");
                for (var i = 0; i < length; i++)
                {
                    AddIfNotBlank(values, match.GetValue($"$[{i}]"));
                }
            }
            else
            {
                AddIfNotBlank(values, match.GetValue("$"));
            }
        }

        if (values.Count != 0)
        {
            return values;
        }

        nodeContext.Warning($"{propertyName} '{path}' resolved to no value; {fallbackHint}");
        return null;
    }

    private static void AddIfNotBlank(ICollection<string> target, object? value)
    {
        var text = value?.ToString();
        if (!string.IsNullOrWhiteSpace(text))
        {
            target.Add(text);
        }
    }

    /// <summary>
    /// Entity scope: the configured runtime ids, else the ones read from the pipeline data. The engine
    /// turns these into an In-filter on the identity column. Shared by the stream-data nodes so the
    /// path handling and the id parsing exist once.
    /// </summary>
    internal static IReadOnlyList<OctoObjectId>? ResolveRtIds(ICollection<string>? configured,
        string? path, IDataContext dataContext, INodeContext nodeContext, string propertyName)
    {
        var values = configured is { Count: > 0 }
            ? configured.ToList()
            : ResolveStringListFromPath(dataContext, nodeContext, path, propertyName,
                "the query is not scoped to specific entities.")?.ToList();

        if (values is not { Count: > 0 })
        {
            return null;
        }

        var rtIds = new List<OctoObjectId>(values.Count);
        foreach (var value in values)
        {
            if (!OctoObjectId.TryParse(value, out var rtId))
            {
                throw MeshAdapterPipelineExecutionException.InvalidRtId(nodeContext, value);
            }

            rtIds.Add(rtId);
        }

        return rtIds;
    }

    /// <summary>
    /// The well-known-name filter AND-combined with the configured field filters. The well-known name
    /// is a standard column on every archive table, so it needs no special handling beyond picking
    /// Equals for a single value and In for several. The configured filters go through
    /// <see cref="ResolveQueryableColumn" />, because a filter the storage layer cannot resolve is
    /// dropped without a word — which widens the result instead of narrowing it.
    /// </summary>
    internal static IReadOnlyList<FieldFilter>? BuildFieldFilters(
        ICollection<string>? wellKnownNames, string? wellKnownNamesPath,
        ICollection<FieldFilterWithPathDto>? fieldFilters, ArchiveSnapshot snapshot,
        StreamDataFieldResolver resolver, IDataContext dataContext, INodeContext nodeContext,
        string wellKnownNamesPathPropertyName)
    {
        var filters = new List<FieldFilter>();

        var names = wellKnownNames is { Count: > 0 }
            ? wellKnownNames.Where(n => !string.IsNullOrWhiteSpace(n)).ToList()
            : ResolveStringListFromPath(dataContext, nodeContext, wellKnownNamesPath,
                wellKnownNamesPathPropertyName,
                "the query is not restricted by well-known name.")?.ToList();

        if (names is { Count: > 0 })
        {
            filters.Add(names.Count == 1
                ? new FieldFilter(WellKnownNameColumn, FieldFilterOperator.Equals, names[0])
                : new FieldFilter(WellKnownNameColumn, FieldFilterOperator.In, names));
        }

        if (fieldFilters is { Count: > 0 })
        {
            // Reuses the shared conversion so the ComparisonValuePath logic (including wildcard
            // expansion) is not duplicated here.
            var scratch = RtEntityQueryOptions.Create();
            fieldFilters.GetFieldFilter(dataContext, scratch);
            if (scratch.FieldFilters != null)
            {
                filters.AddRange(scratch.FieldFilters.Select(f => new FieldFilter(
                    ResolveQueryableColumn(f.AttributePath, snapshot, resolver, nodeContext,
                        "filtering").QueryName,
                    f.Operator, f.ComparisonValue, f.SecondaryValue)));
            }
        }

        return filters.Count == 0 ? null : filters;
    }

    /// <summary>
    /// Reads a projected value out of a <c>StreamDataRow</c> by its storage key — the key the query
    /// layer put it under, as reported by the field resolver. No derivation happens here on purpose:
    /// deriving the key from the attribute path is exactly what broke computed columns (AB#4764).
    /// </summary>
    internal static object? ResolveStreamColumnValue(IReadOnlyDictionary<string, object?> values,
        string storageKey)
    {
        return values.TryGetValue(storageKey, out var value) ? value : null;
    }

    /// <summary>
    /// Reads an aggregate out of a <c>StreamDataRow</c>. The store keys aggregates by
    /// <c>{storageKey}_{funcToken}</c> (e.g. <c>amountvalue_avg</c>) — the column's storage key
    /// suffixed with the lower-case function token, so several aggregations on one column stay
    /// distinct. Falls back to the SQL-alias form <c>{Func}_{storageKey}</c> (e.g.
    /// <c>Avg_amountvalue</c>) that the store also surfaces.
    /// <para>
    /// The storage key comes from the field resolver, not from the attribute path — a computed column
    /// after a formula change lives in a versioned column no derivation can reproduce (AB#4764).
    /// </para>
    /// </summary>
    internal static object? ResolveStreamAggregationValue(IReadOnlyDictionary<string, object?> values,
        string storageKey, Enum aggregationType)
    {
        var token = MapStreamAggregation(aggregationType).KeyToken;

        var outputName = $"{storageKey}_{token}";
        if (values.TryGetValue(outputName, out var v))
        {
            return v;
        }

        var sqlAlias = $"{char.ToUpperInvariant(token[0])}{token[1..]}_{storageKey}";
        return values.TryGetValue(sqlAlias, out var v2) ? v2 : null;
    }

    /// <summary>
    /// Maps an aggregation-type enum to the engine <see cref="AggregationFunction"/> (used to build the
    /// query options) and the lower-case result-key token the storage layer uses when naming the
    /// aggregate output column.
    /// </summary>
    internal static (AggregationFunction Function, string KeyToken) MapStreamAggregation(Enum aggregationType)
    {
        return aggregationType.ToString() switch
        {
            "Count" => (AggregationFunction.Count, "count"),
            "Sum" => (AggregationFunction.Sum, "sum"),
            "Average" => (AggregationFunction.Average, "avg"),
            "Minimum" => (AggregationFunction.Minimum, "min"),
            "Maximum" => (AggregationFunction.Maximum, "max"),
            _ => throw new ArgumentOutOfRangeException(nameof(aggregationType), aggregationType,
                $"Unknown aggregation type: {aggregationType}")
        };
    }

}
