using System.Globalization;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// One revision of the simulation settings as <c>SimulateEnergyMeasurements@2</c> reads it
/// (AB#5631). Unset values carry the simulation defaults.
/// </summary>
internal sealed record SimulationSettingsRevision
{
    public string? RtId { get; init; }
    public DateTime? CreatedUtc { get; init; }
    public DateTime? EffectiveFrom { get; init; }
    public string? Preset { get; init; }
    public int Seed { get; init; } = 1;
    public double ConsumptionFactor { get; init; } = 1.0;
    public double PvCapacityFactor { get; init; } = 1.0;
    public double PvSpecificYield { get; init; } = EnergySimulationMath.DefaultPvSpecificYield;
    public double MemberSpread { get; init; }
    public string? Noise { get; init; }
    public string? Weather { get; init; }
    public bool SupersededByReset { get; init; }

    /// <summary>Date from which this revision applies (set by <see cref="SimulationSettingsTimeline"/>).</summary>
    public DateOnly EffectiveDate { get; init; }

    /// <summary>True for the base revision (smallest <see cref="EffectiveFrom"/>), whose date is not clamped.</summary>
    public bool IsBase { get; init; }

    /// <summary>True when no revision exists and the simulation defaults apply.</summary>
    public bool IsDefault { get; init; }
}

/// <summary>
/// Parses settings revisions out of the DataContext. Accepts the shape <c>GetRtEntitiesByType@1</c>
/// writes (a result set with <c>Items</c>, each a serialized runtime entity with <c>RtId</c>,
/// <c>RtCreationDateTime</c> and the values below <c>Attributes</c>) as well as a flat object or an
/// array of flat objects. Every property name is matched case-insensitively.
/// </summary>
internal static class SimulationSettingsParser
{
    public static List<SimulationSettingsRevision> Parse(JsonNode? node, ICollection<string> warnings)
    {
        var result = new List<SimulationSettingsRevision>();
        if (node == null)
        {
            return result;
        }

        IEnumerable<JsonNode?> elements;
        if (node is JsonArray array)
        {
            elements = array;
        }
        else if (node is JsonObject obj && Property(obj, "Items") is JsonArray items)
        {
            elements = items;
        }
        else
        {
            elements = [node];
        }

        foreach (var element in elements)
        {
            if (element is not JsonObject entry)
            {
                warnings.Add("SimulateEnergyMeasurements@2: a settings entry is not an object and was ignored.");
                continue;
            }

            result.Add(ParseOne(entry));
        }

        return result;
    }

    private static SimulationSettingsRevision ParseOne(JsonObject entry)
    {
        var attributes = Property(entry, "Attributes") as JsonObject ?? entry;
        var defaults = new SimulationSettingsRevision();

        return new SimulationSettingsRevision
        {
            RtId = ReadString(Property(entry, "RtId")),
            CreatedUtc = ReadDateTime(Property(entry, "RtCreationDateTime")),
            EffectiveFrom = ReadDateTime(Property(attributes, "EffectiveFrom")),
            Preset = ReadString(Property(attributes, "Preset")),
            Seed = (int?)ReadDouble(Property(attributes, "Seed")) ?? defaults.Seed,
            ConsumptionFactor = ReadDouble(Property(attributes, "ConsumptionFactor")) ?? defaults.ConsumptionFactor,
            PvCapacityFactor = ReadDouble(Property(attributes, "PvCapacityFactor")) ?? defaults.PvCapacityFactor,
            PvSpecificYield = ReadDouble(Property(attributes, "PvSpecificYield")) ?? defaults.PvSpecificYield,
            MemberSpread = ReadDouble(Property(attributes, "MemberSpread")) ?? defaults.MemberSpread,
            Noise = ReadString(Property(attributes, "Noise")),
            Weather = ReadString(Property(attributes, "Weather")),
            SupersededByReset = ReadBool(Property(attributes, "SupersededByReset")) ?? false
        };
    }

    private static JsonNode? Property(JsonObject obj, string name)
    {
        if (obj.TryGetPropertyValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in obj)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadString(JsonNode? node)
    {
        if (node is not JsonValue)
        {
            return null;
        }

        return node.GetValueKind() switch
        {
            JsonValueKind.String => JsonSerializer.Deserialize<string>(node.ToJsonString()),
            JsonValueKind.Number => node.ToJsonString(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null
        };
    }

    private static double? ReadDouble(JsonNode? node)
    {
        var text = ReadString(node);
        return double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var value)
            ? value
            : null;
    }

    private static bool? ReadBool(JsonNode? node) =>
        ReadString(node) switch
        {
            { } s when bool.TryParse(s, out var b) => b,
            "1" => true,
            "0" => false,
            _ => null
        };

    private static DateTime? ReadDateTime(JsonNode? node)
    {
        var text = ReadString(node);
        if (string.IsNullOrWhiteSpace(text))
        {
            return null;
        }

        if (!DateTime.TryParse(text, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind, out var value))
        {
            return null;
        }

        return value.Kind == DateTimeKind.Local ? value.ToUniversalTime() : value;
    }
}

/// <summary>
/// Selects the settings revision for a simulated day (SIM-08, RST-07). Superseded revisions are
/// ignored. The base revision (smallest <c>EffectiveFrom</c>; ties: earliest creation, then rtId)
/// applies from its <c>EffectiveFrom</c> date and to every earlier day. Every other revision applies
/// from the later of its <c>EffectiveFrom</c> date and its creation date in Europe/Vienna, so a
/// change by the community never acts retroactively. For day D the revision with the greatest
/// effective date not after D wins (ties: later creation, then the greater rtId).
/// </summary>
internal sealed class SimulationSettingsTimeline
{
    private static readonly Lazy<TimeZoneInfo> Vienna = new(ResolveVienna);

    private readonly List<SimulationSettingsRevision> _ordered;

    private SimulationSettingsTimeline(List<SimulationSettingsRevision> ordered, SimulationSettingsRevision baseRevision)
    {
        _ordered = ordered;
        Base = baseRevision;
    }

    public SimulationSettingsRevision Base { get; }

    public IReadOnlyList<SimulationSettingsRevision> Revisions => _ordered;

    public static SimulationSettingsTimeline Build(IEnumerable<SimulationSettingsRevision> parsed,
        ICollection<string> warnings)
    {
        var candidates = new List<SimulationSettingsRevision>();
        var superseded = 0;
        foreach (var revision in parsed)
        {
            if (revision.SupersededByReset)
            {
                superseded++;
                continue;
            }

            if (revision.EffectiveFrom == null)
            {
                warnings.Add(
                    $"SimulateEnergyMeasurements@2: settings revision '{revision.RtId ?? "(no rtId)"}' has no EffectiveFrom and was ignored.");
                continue;
            }

            candidates.Add(revision);
        }

        if (candidates.Count == 0)
        {
            warnings.Add(superseded > 0
                ? "SimulateEnergyMeasurements@2: every settings revision is superseded by a reset; the simulation defaults apply (seed 1, factors 1.0, specific yield 1050, spread 0)."
                : "SimulateEnergyMeasurements@2: no simulation settings found; the simulation defaults apply (seed 1, factors 1.0, specific yield 1050, spread 0).");
            var defaults = new SimulationSettingsRevision { IsBase = true, IsDefault = true, EffectiveDate = DateOnly.MinValue };
            return new SimulationSettingsTimeline([defaults], defaults);
        }

        var baseRevision = candidates
            .OrderBy(r => ToViennaDate(r.EffectiveFrom!.Value))
            .ThenBy(r => r.EffectiveFrom!.Value)
            .ThenBy(r => r.CreatedUtc ?? DateTime.MaxValue)
            .ThenBy(r => r.RtId ?? string.Empty, StringComparer.Ordinal)
            .First();

        var resolved = candidates.Select(r =>
        {
            var effectiveFromDate = ToViennaDate(r.EffectiveFrom!.Value);
            if (ReferenceEquals(r, baseRevision))
            {
                return r with { IsBase = true, EffectiveDate = effectiveFromDate };
            }

            var effective = effectiveFromDate;
            if (r.CreatedUtc is { } created)
            {
                var createdDate = ToViennaDate(created);
                if (createdDate > effective)
                {
                    effective = createdDate;
                }
            }

            return r with { EffectiveDate = effective };
        }).ToList();

        // Ascending, so the LAST entry not after a day is the winner (ties: later creation, greater rtId).
        var ordered = resolved
            .OrderBy(r => r.EffectiveDate)
            .ThenBy(r => r.CreatedUtc ?? DateTime.MinValue)
            .ThenBy(r => r.RtId ?? string.Empty, StringComparer.Ordinal)
            .ToList();

        var timeline = new SimulationSettingsTimeline(ordered, ordered.Single(r => r.IsBase));
        timeline.AddRevisionWarnings(warnings);
        return timeline;
    }

    /// <summary>Revision that applies to <paramref name="day"/>.</summary>
    public SimulationSettingsRevision For(DateOnly day)
    {
        SimulationSettingsRevision? winner = null;
        foreach (var revision in _ordered)
        {
            if (revision.EffectiveDate > day)
            {
                break;
            }

            winner = revision;
        }

        return winner ?? Base;
    }

    private void AddRevisionWarnings(ICollection<string> warnings)
    {
        if (_ordered.Any(r => !IsOff(r.Noise) || !IsOff(r.Weather)))
        {
            warnings.Add(
                "SimulateEnergyMeasurements@2: Noise and Weather are not supported before M3 and were ignored.");
        }

        foreach (var revision in _ordered.Where(r => !r.IsBase && !string.Equals(r.Preset, Base.Preset, StringComparison.OrdinalIgnoreCase)))
        {
            warnings.Add(
                $"SimulateEnergyMeasurements@2: settings revision '{revision.RtId ?? "(no rtId)"}' changes the preset from '{Base.Preset}' to '{revision.Preset}'; a preset change takes effect only through a reset and was ignored.");
        }

        if (_ordered.Any(r => r.PvSpecificYield <= 0))
        {
            warnings.Add(
                "SimulateEnergyMeasurements@2: a PvSpecificYield of 0 or less was replaced by 1050 kWh/kWp.");
        }
    }

    private static bool IsOff(string? value) =>
        string.IsNullOrWhiteSpace(value)
        || value == "0"
        || string.Equals(value, "Off", StringComparison.OrdinalIgnoreCase)
        || string.Equals(value, "ClearSky", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// Calendar date in Europe/Vienna. A value without a time zone (kind Unspecified, e.g. a plain
    /// <c>2026-11-01</c>) is taken as that date.
    /// </summary>
    public static DateOnly ToViennaDate(DateTime value)
    {
        if (value.Kind == DateTimeKind.Unspecified)
        {
            return DateOnly.FromDateTime(value);
        }

        return DateOnly.FromDateTime(TimeZoneInfo.ConvertTimeFromUtc(value.ToUniversalTime(), Vienna.Value));
    }

    private static TimeZoneInfo ResolveVienna()
    {
        // IANA id first (Linux, macOS, Windows with ICU); the Windows id as a fallback.
        foreach (var id in new[] { "Europe/Vienna", "W. Europe Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (TimeZoneNotFoundException)
            {
            }
            catch (InvalidTimeZoneException)
            {
            }
        }

        throw new InvalidOperationException(
            "SimulateEnergyMeasurements@2: time zone Europe/Vienna is not available on this host.");
    }
}
