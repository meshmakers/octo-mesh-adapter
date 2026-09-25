using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Runtime.Contracts.StreamData;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// One series of windowed values that resolve to a single anchor entity.
/// </summary>
internal sealed class ShapedSeries(string wellKnownName, JsonObject series)
{
    private readonly Dictionary<JsonObject, JsonObject> _sourceOf = new(ReferenceEqualityComparer.Instance);

    /// <summary>Natural key of the anchor entity this series belongs to.</summary>
    public string WellKnownName { get; } = wellKnownName;

    /// <summary>
    /// The first series object seen for this key — the source of the parent RtId. Several source
    /// objects legitimately resolve to one key: a producer that splits a logical series across blocks,
    /// or one batch carrying several documents for the same register.
    /// </summary>
    public JsonObject Series { get; } = series;

    public List<JsonObject> Values { get; } = [];

    /// <summary>Adds a value together with the series object it came from.</summary>
    public void Add(JsonObject value, JsonObject source)
    {
        Values.Add(value);
        _sourceOf[value] = source;
    }

    /// <summary>
    /// The series object a value came from — the source of its series-scoped column values. Those
    /// differ between the objects merged under one key: two EDA documents for the same register carry
    /// their own document date, and the archive's ConflictPrecedence orders by it. Taking them from
    /// the first object stamped every row of a batch with the first document's date, so a same-quality
    /// correction in the same batch won or lost by position instead of by date.
    /// </summary>
    public JsonObject SourceOf(JsonObject value) => _sourceOf.GetValueOrDefault(value, Series);

    /// <summary>RtId the anchor is written under; assigned once the runtime store has been queried.</summary>
    public OctoObjectId RtId { get; set; }
}

/// <summary>
/// The pure, storage-free half of <see cref="SaveTimeRangeSeriesInArchiveNode" />: turning a series
/// document into anchors and archive rows. Kept separate so the shaping rules — which is the anchor's
/// natural key, which value the anchor reflects, which values are unusable — can be tested without a
/// CK model, a repository or a database, the way <c>StreamDataGapAnalyzer</c> is.
/// </summary>
internal static class TimeRangeSeriesShaper
{
    /// <summary>
    /// Groups the series document by anchor key and drops what cannot be written: a series whose
    /// key does not resolve, and a key that ends up with no values at all (an anchor entity with
    /// nothing behind it is a placeholder nobody asked for).
    /// </summary>
    /// <param name="seriesArray">The series document as it arrives from the data context.</param>
    /// <param name="c">The node configuration naming the key format and the values property.</param>
    /// <param name="unresolvedKeys">
    /// Number of series objects skipped because their well-known name did not resolve — reported by
    /// the caller, since a silently missing series is exactly what this node must not produce.
    /// </param>
    public static List<ShapedSeries> Shape(
        JsonArray seriesArray,
        SaveTimeRangeSeriesInArchiveNodeConfiguration c,
        out int unresolvedKeys)
    {
        var byKey = new Dictionary<string, ShapedSeries>(StringComparer.Ordinal);
        var order = new List<ShapedSeries>();
        unresolvedKeys = 0;

        foreach (var seriesNode in seriesArray)
        {
            if (seriesNode is not JsonObject series)
            {
                continue;
            }

            var wellKnownName = FormatWellKnownName(c.WellKnownNameFormat, series);
            if (string.IsNullOrEmpty(wellKnownName))
            {
                unresolvedKeys++;
                continue;
            }

            if (series[c.ValuesProperty] is not JsonArray values)
            {
                continue;
            }

            if (!byKey.TryGetValue(wellKnownName, out var shaped))
            {
                shaped = new ShapedSeries(wellKnownName, series);
                byKey[wellKnownName] = shaped;
                order.Add(shaped);
            }

            foreach (var valueNode in values)
            {
                if (valueNode is JsonObject value)
                {
                    shaped.Add(value, series);
                }
            }
        }

        return order.Where(s => s.Values.Count > 0).ToList();
    }

    /// <summary>
    /// Substitutes <c>{PropertyName}</c> placeholders with the series object's property values.
    /// </summary>
    /// <remarks>
    /// Returns an empty string when any referenced property is missing, null or not a scalar, so a
    /// partially resolved key can never be mistaken for a real one — two different series resolving
    /// to the same half-built key would silently merge into one anchor.
    /// </remarks>
    public static string FormatWellKnownName(string format, JsonObject series)
    {
        var result = new StringBuilder(format.Length);
        var i = 0;
        while (i < format.Length)
        {
            var open = format.IndexOf('{', i);
            if (open < 0)
            {
                result.Append(format, i, format.Length - i);
                break;
            }

            var close = format.IndexOf('}', open + 1);
            if (close < 0)
            {
                result.Append(format, i, format.Length - i);
                break;
            }

            result.Append(format, i, open - i);
            var scalar = ToScalar(series[format.Substring(open + 1, close - open - 1)], parseDateStrings: false);
            if (scalar is null)
            {
                return string.Empty;
            }

            result.Append(Convert.ToString(scalar, CultureInfo.InvariantCulture));
            i = close + 1;
        }

        return result.ToString();
    }

    /// <summary>
    /// Picks the value the anchor entity reflects: the one with the latest window end when the
    /// anchor carries a window, otherwise the last one in document order.
    /// </summary>
    public static JsonObject SelectAnchorValue(
        IReadOnlyList<JsonObject> values, SaveTimeRangeSeriesInArchiveNodeConfiguration c)
    {
        if (c.AnchorWindowToAttribute is null)
        {
            return values[^1];
        }

        var winner = values[0];
        var winnerEnd = ReadDateTime(winner, c.ToProperty);
        foreach (var candidate in values)
        {
            var end = ReadDateTime(candidate, c.ToProperty);
            if (end is null)
            {
                continue;
            }

            if (winnerEnd is null || end.Value > winnerEnd.Value)
            {
                winner = candidate;
                winnerEnd = end;
            }
        }

        return winner;
    }

    /// <summary>
    /// Maps every value of every shaped series onto one archive row.
    /// </summary>
    /// <param name="series">The shaped series, each already carrying its resolved anchor RtId.</param>
    /// <param name="ckTypeId">CK type of the anchor entity, stamped onto every row.</param>
    /// <param name="c">The node configuration naming the window properties and the columns.</param>
    /// <param name="convert">
    /// Maps a raw JSON scalar onto the value the archive column stores, given the column's attribute
    /// path. Supplied by the node because it needs the CK model: an Enum attribute is stored as its
    /// integer KEY, so a raw <c>"L1"</c> has to become <c>1</c> or CrateDB rejects the write against
    /// an integer column. Everything else passes through.
    /// </param>
    /// <param name="skippedNoWindow">
    /// Values dropped for lacking a usable <c>[from, to)</c> window. One malformed slot must not cost
    /// the rest of a bulk replay, so they are counted and reported rather than thrown on.
    /// </param>
    public static List<TimeRangeStreamDataPoint> BuildRows(
        IReadOnlyList<ShapedSeries> series,
        RtCkId<CkTypeId> ckTypeId,
        SaveTimeRangeSeriesInArchiveNodeConfiguration c,
        Func<string, object?, object?> convert,
        out int skippedNoWindow)
    {
        var points = new List<TimeRangeStreamDataPoint>(series.Sum(s => s.Values.Count));
        skippedNoWindow = 0;

        foreach (var shaped in series)
        {
            // Series-scoped values are constant per source object — resolve them once per object
            // rather than per value; a long series is thousands of values.
            var seriesValuesBySource =
                new Dictionary<JsonObject, List<KeyValuePair<string, object?>>>(ReferenceEqualityComparer.Instance);

            foreach (var value in shaped.Values)
            {
                var source = shaped.SourceOf(value);
                if (!seriesValuesBySource.TryGetValue(source, out var seriesValues))
                {
                    seriesValues = [];
                    foreach (var column in c.Columns.Where(x => x.Scope == TimeRangeSeriesColumnScope.Series))
                    {
                        var scalar = convert(column.Name, ColumnScalar(source[column.ValueProperty]));
                        if (scalar is not null)
                        {
                            seriesValues.Add(new KeyValuePair<string, object?>(column.Name, scalar));
                        }
                    }

                    seriesValuesBySource[source] = seriesValues;
                }

                var from = ReadDateTime(value, c.FromProperty);
                var to = ReadDateTime(value, c.ToProperty);
                if (from is null || to is null || to <= from)
                {
                    skippedNoWindow++;
                    continue;
                }

                var attributes = new Dictionary<string, object?>(c.Columns.Count, StringComparer.Ordinal);
                foreach (var kv in seriesValues)
                {
                    attributes[kv.Key] = kv.Value;
                }

                foreach (var column in c.Columns.Where(x => x.Scope == TimeRangeSeriesColumnScope.Value))
                {
                    var scalar = convert(column.Name, ColumnScalar(value[column.ValueProperty]));
                    if (scalar is not null)
                    {
                        attributes[column.Name] = scalar;
                    }
                }

                points.Add(new TimeRangeStreamDataPoint
                {
                    From = from.Value,
                    To = to.Value,
                    RtId = shaped.RtId,
                    RtWellKnownName = shaped.WellKnownName,
                    CkTypeId = ckTypeId,
                    Attributes = attributes
                });
            }
        }

        return points;
    }

    /// <summary>
    /// Reads a property as a UTC <see cref="DateTime" />, tolerating the forms a JSON document can
    /// carry it in (a real date, or an ISO-8601 string that survived a round trip as text).
    /// </summary>
    public static DateTime? ReadDateTime(JsonObject source, string property)
        => ToScalar(source[property]) switch
        {
            DateTime dt => NormaliseToUtc(dt),
            DateTimeOffset dto => dto.UtcDateTime,
            string s when DateTime.TryParse(s, CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed) => parsed,
            _ => null
        };

    /// <summary>
    /// Unwraps a JSON node to its natural CLR scalar. <see cref="JsonScalar" /> deliberately offers
    /// no <see cref="JsonNode" /> overload — a node may be an object or an array, which has no scalar
    /// form — so non-scalar nodes resolve to null here rather than to a stringified blob.
    /// </summary>
    public static object? ToScalar(JsonNode? node, bool parseDateStrings = true)
        => node is JsonValue value ? JsonScalar.ToClr(value, parseDateStrings) : null;

    /// <summary>
    /// A column value as the archive stores it: a date in UTC, like the window boundaries. A date
    /// string carrying an offset ("+02:00", as some grid operators write DocumentCreationDateTime)
    /// parses to a LOCAL DateTime, and the archive would store that wall clock as UTC — shifted by the
    /// host's offset, invisible in a UTC container and two hours off on a CEST laptop.
    /// </summary>
    private static object? ColumnScalar(JsonNode? node) => ToScalar(node) switch
    {
        DateTime dt => NormaliseToUtc(dt),
        DateTimeOffset dto => dto.UtcDateTime,
        var other => other
    };

    /// <summary>
    /// Treats an unspecified kind as UTC, matching how the archive stores and returns window
    /// boundaries. Left local, a comparison against a stored value would silently shift by the
    /// host's offset.
    /// </summary>
    public static DateTime NormaliseToUtc(DateTime value) => value.Kind switch
    {
        DateTimeKind.Utc => value,
        DateTimeKind.Local => value.ToUniversalTime(),
        _ => DateTime.SpecifyKind(value, DateTimeKind.Utc)
    };
}
