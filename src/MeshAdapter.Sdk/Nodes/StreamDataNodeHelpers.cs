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
/// <c>GetStreamData@1</c>): UTC normalisation of configured time-range boundaries, reading those
/// boundaries from the pipeline data via JSONPath, and resolving projected / aggregated values out
/// of a <c>StreamDataRow</c>'s physically-named value dictionary.
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
    internal static string ResolveQueryableColumn(string name, ArchiveSnapshot snapshot,
        INodeContext nodeContext, string usage)
    {
        var windowed = snapshot.UsesWindowedStorage;

        // The node's own result vocabulary. Timestamp is the logical time axis: window_end on a
        // windowed archive, the timestamp column on a raw one.
        if (Matches(name, "Timestamp"))
        {
            return windowed ? WindowEndColumn : TimestampColumn;
        }

        // Only a windowed archive has a row window. On a raw archive these names fall through to the
        // validation below and are rejected — resolving them anyway would hand the storage layer a
        // column that does not exist, and it would drop it silently again.
        if (windowed && Matches(name, "WindowStart")) return WindowStartColumn;
        if (windowed && Matches(name, "WindowEnd")) return WindowEndColumn;

        if (Matches(name, "WellKnownName")) return WellKnownNameColumn;

        // Names the storage layer resolves as-is: the standard columns of this storage shape, and
        // the archive's own columns (ingested by path, computed by name).
        var defaults = Constants.GetDefaultStreamDataFields(windowed);
        if (defaults.Any(f => Matches(name, f)))
        {
            return name;
        }

        if (snapshot.Columns.Any(spec =>
                (!string.IsNullOrWhiteSpace(spec.Path) && Matches(name, spec.Path))
                || (!string.IsNullOrWhiteSpace(spec.Name) && Matches(name, spec.Name!))))
        {
            return name;
        }

        throw MeshAdapterPipelineExecutionException.UnknownStreamDataColumn(
            nodeContext, name, usage, BuildKnownColumnList(snapshot, defaults));
    }

    private static bool Matches(string a, string b) => string.Equals(a, b, StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// The names a caller may use, for the error message: the node's result headers first (what a
    /// reader of the output would reach for), then the archive's own columns.
    /// </summary>
    private static string BuildKnownColumnList(ArchiveSnapshot snapshot, IReadOnlyList<string> defaults)
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
        names.AddRange(defaults);

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
        IDataContext dataContext, INodeContext nodeContext, string wellKnownNamesPathPropertyName)
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
                    ResolveQueryableColumn(f.AttributePath, snapshot, nodeContext, "filtering"),
                    f.Operator, f.ComparisonValue, f.SecondaryValue)));
            }
        }

        return filters.Count == 0 ? null : filters;
    }

    /// <summary>
    /// Resolves a projected column value from a <c>StreamDataRow</c>. The stream-data store keys the
    /// row's values by the physical CrateDB column name — the attribute path stripped of its dot
    /// separators and lower-cased (see the storage layer's <c>ColumnNameMapper.PathToColumnName</c>).
    /// Standard columns such as <c>window_start</c> or <c>was_updated</c> already equal their physical
    /// name and match directly; dotted / mixed-case attribute paths such as <c>amount.value</c> or
    /// <c>obisCode</c> only match after normalisation. Tries the exact key first (cheap, covers the
    /// standard columns) and falls back to the normalised form.
    /// </summary>
    internal static object? ResolveStreamColumnValue(IReadOnlyDictionary<string, object?> values,
        string attributePath)
    {
        if (values.TryGetValue(attributePath, out var direct))
        {
            return direct;
        }

        var physicalColumnName = ToPhysicalColumnName(attributePath);
        return values.TryGetValue(physicalColumnName, out var mapped) ? mapped : null;
    }

    /// <summary>
    /// Resolves an aggregation value from a <c>StreamDataRow</c>. The stream-data store keys aggregate
    /// results by the friendly output name <c>{physicalColumn}_{funcToken}</c> (e.g.
    /// <c>amountvalue_avg</c>) — the attribute path stripped of dots and lower-cased, suffixed with the
    /// lower-case function token. Falls back to the SQL-alias form <c>{Func}_{physicalColumn}</c>
    /// (e.g. <c>Avg_amountvalue</c>) that the store also surfaces.
    /// </summary>
    internal static object? ResolveStreamAggregationValue(IReadOnlyDictionary<string, object?> values,
        string attributePath, Enum aggregationType)
    {
        var token = MapStreamAggregation(aggregationType).KeyToken;
        var column = ToPhysicalColumnName(attributePath);

        var outputName = $"{column}_{token}";
        if (values.TryGetValue(outputName, out var v))
        {
            return v;
        }

        var sqlAlias = $"{char.ToUpperInvariant(token[0])}{token[1..]}_{column}";
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

    /// <summary>
    /// Attribute path to physical CrateDB column name: dots stripped, lower-cased.
    /// </summary>
    private static string ToPhysicalColumnName(string attributePath)
    {
        return attributePath.Replace(".", string.Empty).ToLowerInvariant();
    }
}
