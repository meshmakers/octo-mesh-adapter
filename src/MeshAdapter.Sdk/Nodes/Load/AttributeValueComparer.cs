using System.Collections;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;

/// <summary>
/// Structural comparison and readable, truncated formatting of runtime attribute values
/// (AB#7436). Attribute values reaching <c>ApplyChanges@2</c> are scalars, <see cref="RtRecord"/>s,
/// lists of those, dictionaries (<c>Dictionary&lt;string, object?&gt;</c>) and - when they come out of
/// pipeline data - <see cref="JsonElement"/>/<see cref="JsonNode"/> trees. Two values are equal when
/// their content is equal, regardless of the instances and of the numeric CLR type.
/// </summary>
internal static class AttributeValueComparer
{
    /// <summary>Maximum nesting depth followed before a value is treated as opaque.</summary>
    private const int MaxDepth = 32;

    /// <summary>Marker printed instead of the value of a sensitive attribute.</summary>
    internal const string RedactedMarker = "<redacted>";

    private static readonly string[] SensitiveNameParts =
        ["password", "secret", "token", "apikey", "credential", "privatekey"];

    /// <summary>
    /// Returns true when both values have the same content: lists element by element, dictionaries and
    /// records key by key, numbers by value (int, long, decimal and double of the same value are equal),
    /// date/times by instant, <c>null</c> only equal to <c>null</c>.
    /// </summary>
    public static bool AreEqual(object? left, object? right)
    {
        return AreEqual(left, right, 0);
    }

    /// <summary>
    /// Formats a value as compact JSON-like text, truncated to <paramref name="maxLength"/> characters.
    /// </summary>
    public static string Format(object? value, int maxLength = 200)
    {
        var sb = new StringBuilder();
        Write(sb, value, maxLength, 0);
        if (sb.Length <= maxLength)
        {
            return sb.ToString();
        }

        return sb.ToString(0, maxLength) + "…";
    }

    /// <summary>
    /// Like <see cref="Format"/>, but prints <see cref="RedactedMarker"/> for attributes whose name
    /// suggests a credential (the pipeline carries plaintext secrets up to the engine, AB#5538).
    /// </summary>
    public static string FormatAttribute(string attributeName, object? value, int maxLength = 200)
    {
        return IsSensitive(attributeName, value) ? RedactedMarker : Format(value, maxLength);
    }

    private static bool IsSensitive(string attributeName, object? value)
    {
        if (value is RtSecretValue)
        {
            return true;
        }

        return SensitiveNameParts.Any(p => attributeName.Contains(p, StringComparison.OrdinalIgnoreCase));
    }

    private static bool AreEqual(object? left, object? right, int depth)
    {
        left = Unwrap(left);
        right = Unwrap(right);

        if (ReferenceEquals(left, right))
        {
            return true;
        }

        if (left is null || right is null)
        {
            return false;
        }

        if (depth >= MaxDepth)
        {
            return Equals(left, right);
        }

        if (left is string ls)
        {
            return right is string rs && string.Equals(ls, rs, StringComparison.Ordinal);
        }

        if (right is string)
        {
            return false;
        }

        if (IsNumber(left) || IsNumber(right))
        {
            return IsNumber(left) && IsNumber(right) && NumbersEqual(left, right);
        }

        if (TryGetInstant(left, out var li) || TryGetInstant(right, out _))
        {
            return TryGetInstant(right, out var ri) && li == ri;
        }

        if (left is RtTypeWithAttributes lr)
        {
            return right is RtTypeWithAttributes rr && RecordsEqual(lr, rr, depth);
        }

        if (right is RtTypeWithAttributes)
        {
            return false;
        }

        var leftDict = AsDictionary(left);
        var rightDict = AsDictionary(right);
        if (leftDict != null || rightDict != null)
        {
            return leftDict != null && rightDict != null && DictionariesEqual(leftDict, rightDict, depth);
        }

        if (left is byte[] lb && right is byte[] rb)
        {
            return lb.AsSpan().SequenceEqual(rb);
        }

        if (left is IEnumerable le && right is IEnumerable re)
        {
            return SequencesEqual(le, re, depth);
        }

        return Equals(left, right);
    }

    private static bool RecordsEqual(RtTypeWithAttributes left, RtTypeWithAttributes right, int depth)
    {
        if (left is RtRecord lr && right is RtRecord rr && !Equals(lr.CkRecordId, rr.CkRecordId))
        {
            return false;
        }

        return DictionariesEqual(
            left.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value),
            right.Attributes.ToDictionary(kvp => kvp.Key, kvp => kvp.Value), depth);
    }

    private static bool DictionariesEqual(IReadOnlyDictionary<string, object?> left,
        IReadOnlyDictionary<string, object?> right, int depth)
    {
        if (left.Count != right.Count)
        {
            return false;
        }

        foreach (var (key, value) in left)
        {
            if (!right.TryGetValue(key, out var other) || !AreEqual(value, other, depth + 1))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SequencesEqual(IEnumerable left, IEnumerable right, int depth)
    {
        var le = left.GetEnumerator();
        var re = right.GetEnumerator();
        try
        {
            while (true)
            {
                var hasLeft = le.MoveNext();
                var hasRight = re.MoveNext();
                if (hasLeft != hasRight)
                {
                    return false;
                }

                if (!hasLeft)
                {
                    return true;
                }

                if (!AreEqual(le.Current, re.Current, depth + 1))
                {
                    return false;
                }
            }
        }
        finally
        {
            (le as IDisposable)?.Dispose();
            (re as IDisposable)?.Dispose();
        }
    }

    /// <summary>Converts JSON trees to plain CLR values (dictionary, list, string, number, bool, null).</summary>
    private static object? Unwrap(object? value)
    {
        switch (value)
        {
            case JsonNode node:
                return Unwrap(JsonSerializer.SerializeToElement(node));
            case JsonElement element:
                return element.ValueKind switch
                {
                    JsonValueKind.Object => element.EnumerateObject()
                        .ToDictionary(p => p.Name, p => (object?)p.Value),
                    JsonValueKind.Array => element.EnumerateArray().Select(e => (object?)e).ToList(),
                    JsonValueKind.String => element.GetString(),
                    JsonValueKind.Number => element.TryGetDecimal(out var d) ? d : element.GetDouble(),
                    JsonValueKind.True => true,
                    JsonValueKind.False => false,
                    _ => null
                };
            default:
                return value;
        }
    }

    private static IReadOnlyDictionary<string, object?>? AsDictionary(object value)
    {
        switch (value)
        {
            case IReadOnlyDictionary<string, object?> ro:
                return ro;
            case IDictionary<string, object?> rw:
                return new Dictionary<string, object?>(rw);
            case IDictionary legacy:
            {
                var result = new Dictionary<string, object?>(legacy.Count);
                foreach (DictionaryEntry entry in legacy)
                {
                    result[Convert.ToString(entry.Key, CultureInfo.InvariantCulture) ?? string.Empty] = entry.Value;
                }

                return result;
            }
            default:
                return null;
        }
    }

    private static bool IsNumber(object value)
    {
        return value is sbyte or byte or short or ushort or int or uint or long or ulong or float or double
            or decimal;
    }

    private static bool NumbersEqual(object left, object right)
    {
        if (left is float or double || right is float or double)
        {
            return Convert.ToDouble(left, CultureInfo.InvariantCulture)
                .Equals(Convert.ToDouble(right, CultureInfo.InvariantCulture));
        }

        return Convert.ToDecimal(left, CultureInfo.InvariantCulture) ==
               Convert.ToDecimal(right, CultureInfo.InvariantCulture);
    }

    private static bool TryGetInstant(object value, out DateTime utc)
    {
        switch (value)
        {
            case DateTime dt:
                utc = dt.Kind == DateTimeKind.Unspecified ? DateTime.SpecifyKind(dt, DateTimeKind.Utc) : dt.ToUniversalTime();
                return true;
            case DateTimeOffset dto:
                utc = dto.UtcDateTime;
                return true;
            default:
                utc = default;
                return false;
        }
    }

    private static void Write(StringBuilder sb, object? value, int budget, int depth)
    {
        if (sb.Length > budget)
        {
            return;
        }

        value = Unwrap(value);
        switch (value)
        {
            case null:
                sb.Append("null");
                return;
            case string s:
                sb.Append('"').Append(s.Length > budget ? s[..budget] : s).Append('"');
                return;
            case bool b:
                sb.Append(b ? "true" : "false");
                return;
            case DateTime dt:
                sb.Append('"').Append(dt.ToString("o", CultureInfo.InvariantCulture)).Append('"');
                return;
            case DateTimeOffset dto:
                sb.Append('"').Append(dto.ToString("o", CultureInfo.InvariantCulture)).Append('"');
                return;
            case IFormattable f when IsNumber(value):
                sb.Append(f.ToString(null, CultureInfo.InvariantCulture));
                return;
            case RtSecretValue:
                sb.Append(RedactedMarker);
                return;
        }

        if (depth >= MaxDepth)
        {
            sb.Append('…');
            return;
        }

        if (value is RtTypeWithAttributes record)
        {
            sb.Append('{');
            var first = true;
            if (record is RtRecord rtRecord)
            {
                sb.Append("\"$ckRecordId\":\"").Append(rtRecord.CkRecordId).Append('"');
                first = false;
            }

            foreach (var kvp in record.Attributes)
            {
                if (sb.Length > budget) break;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kvp.Key).Append("\":");
                Write(sb, IsSensitive(kvp.Key, kvp.Value) ? RedactedMarker : kvp.Value, budget, depth + 1);
            }

            sb.Append('}');
            return;
        }

        var dict = AsDictionary(value!);
        if (dict != null)
        {
            sb.Append('{');
            var first = true;
            foreach (var kvp in dict)
            {
                if (sb.Length > budget) break;
                if (!first) sb.Append(',');
                first = false;
                sb.Append('"').Append(kvp.Key).Append("\":");
                Write(sb, IsSensitive(kvp.Key, kvp.Value) ? RedactedMarker : kvp.Value, budget, depth + 1);
            }

            sb.Append('}');
            return;
        }

        if (value is IEnumerable sequence && value is not byte[])
        {
            sb.Append('[');
            var first = true;
            foreach (var item in sequence)
            {
                if (sb.Length > budget) break;
                if (!first) sb.Append(',');
                first = false;
                Write(sb, item, budget, depth + 1);
            }

            sb.Append(']');
            return;
        }

        sb.Append(Convert.ToString(value, CultureInfo.InvariantCulture));
    }
}
