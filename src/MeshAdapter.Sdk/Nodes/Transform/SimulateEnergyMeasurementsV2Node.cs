using System.Globalization;
using System.Text.Json.Nodes;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.SimulationNodes.Generators;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// SimulateEnergyMeasurements@2 (AB#5631): backfills the raw channels of existing EnergyMeasurement
/// anchors of SIMULATED metering points, sized per metering point and steered by settings revisions.
/// Version 1 stays unchanged next to it.
/// </summary>
[NodeConfiguration(typeof(SimulateEnergyMeasurementsV2NodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class SimulateEnergyMeasurementsV2Node(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    private const string NodeLabel = "SimulateEnergyMeasurements@2";

    internal const string SkipNoParent = "NoParentMeteringPoint";
    internal const string SkipNotARawChannel = "NotARawChannel";
    internal const string SkipNotSimulated = "DataSourceNotSimulated";
    internal const string SkipUnsupportedProductionType = "UnsupportedProductionType";

    private static readonly string[] LoadProfileByEnum = ["H0", "G0", "L0"];

    private static readonly string[] ProductionTypeNames = ["Unknown", "Solar", "HEP", "Wind", "Other", "Biomass", "CHP"];

    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SimulateEnergyMeasurementsV2NodeConfiguration>();

        var startUtc = DateTime.SpecifyKind(ResolveStartDate(c, dataContext), DateTimeKind.Utc);
        var numDays = ResolveNumDays(c, dataContext);
        if (!EnergyProfiles.LoadProfileWeights.ContainsKey(c.DefaultLoadProfile))
        {
            throw new InvalidOperationException(
                $"{NodeLabel}: DefaultLoadProfile '{c.DefaultLoadProfile}' is not supported (H0, G0, L0).");
        }

        var warnings = new List<string>();

        // Settings first: a malformed settings document should not cost a repository round trip.
        var settingsNode = string.IsNullOrWhiteSpace(c.SettingsPath) ? null : dataContext.Get<JsonNode>(c.SettingsPath);
        var timeline = SimulationSettingsTimeline.Build(SimulationSettingsParser.Parse(settingsNode, warnings), warnings);

        var emCkType = new RtCkId<CkTypeId>(c.EnergyMeasurementCkTypeId);
        var meteringPointCkType = new RtCkId<CkTypeId>(c.MeteringPointCkTypeId);
        var producerCkType = new RtCkId<CkTypeId>(c.ProducerCkTypeId);
        var roleId = new RtCkId<CkAssociationRoleId>(c.ParentAssociationRoleId);

        // AB#5028 — scoped: reads the tenant's own business data (energy measurement anchors and
        // their metering points), exactly like version 1.
        var session = await etlContext.GetSessionForAsync(c.Identity);
        session.StartTransaction();

        var emResult = await etlContext.TenantRepository.GetRtEntitiesByTypeAsync(
            session, emCkType, RtEntityQueryOptions.Create(), 0, int.MaxValue);
        var existingEms = emResult.Items.ToList();

        var parentByEmRtId = new Dictionary<OctoObjectId, RtEntity>();
        if (existingEms.Count > 0)
        {
            var parentResult = await etlContext.TenantRepository.GetRtAssociationTargetsAsync(
                session, existingEms.Select(e => e.RtId).ToArray(), emCkType, roleId, meteringPointCkType,
                GraphDirections.Outbound, null, RtEntityQueryOptions.Create());
            foreach (var kvp in parentResult)
            {
                var parent = kvp.Value.Items.FirstOrDefault();
                if (parent != null)
                {
                    parentByEmRtId[kvp.Key.RtId] = parent;
                }
            }
        }

        await session.CommitTransactionAsync();

        var skipped = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var anchors = SelectAnchors(c, existingEms, parentByEmRtId, producerCkType, skipped, warnings);

        // Revision per simulated day (UTC date of the day start).
        var revisionByDay = new SimulationSettingsRevision[numDays];
        for (var day = 0; day < numDays; day++)
        {
            revisionByDay[day] = timeline.For(DateOnly.FromDateTime(startUtc.AddDays(day)));
        }

        var datapoints = new List<EntityUpdateInfo<RtEntity>>(anchors.Count * numDays * EnergySimulationMath.SlotsPerDay);
        var kWhByObis = new SortedDictionary<string, double>(StringComparer.Ordinal);
        var timeRangeRecordId = new RtCkId<CkRecordId>(c.TimeRangeCkRecordId);
        var amountRecordId = new RtCkId<CkRecordId>(c.AmountCkRecordId);
        var slotTicks = TimeSpan.FromMinutes(15).Ticks;
        var changed = DateTime.UtcNow;

        foreach (var anchor in anchors)
        {
            var total = 0.0;
            for (var day = 0; day < numDays; day++)
            {
                var revision = revisionByDay[day];
                var dayStart = startUtc.AddDays(day);
                var spread = EnergySimulationMath.MemberSpreadFactor(revision.Seed, anchor.MeteringPointNumber,
                    revision.MemberSpread);

                double[]? weights = null;
                var dailyKWh = 0.0;
                var kWp = 0.0;
                var specificYield = revision.PvSpecificYield > 0
                    ? revision.PvSpecificYield
                    : EnergySimulationMath.DefaultPvSpecificYield;
                if (anchor.IsProducer)
                {
                    kWp = anchor.Magnitude * revision.PvCapacityFactor * spread;
                }
                else
                {
                    weights = EnergyProfiles.LoadProfileWeights[anchor.LoadProfile!];
                    dailyKWh = anchor.Magnitude * revision.ConsumptionFactor * spread / 365.0;
                }

                for (var slot = 0; slot < EnergySimulationMath.SlotsPerDay; slot++)
                {
                    var raw = anchor.IsProducer
                        ? EnergySimulationMath.PvSlotKWh(kWp, dayStart.DayOfYear, slot, specificYield, c.SolarNoonUtcHour)
                        : weights![slot] * dailyKWh;
                    var amount = EnergySimulationMath.Round(raw);
                    total += amount;

                    var from = dayStart.AddTicks(slotTicks * slot);
                    var timeRange = new RtRecord { CkRecordId = timeRangeRecordId };
                    timeRange.SetAttributeValue("From", AttributeValueTypesDto.DateTime, from);
                    timeRange.SetAttributeValue("To", AttributeValueTypesDto.DateTime, from.AddTicks(slotTicks));

                    var amountRecord = new RtRecord { CkRecordId = amountRecordId };
                    amountRecord.SetAttributeValue("Value", AttributeValueTypesDto.Double, amount);
                    amountRecord.SetAttributeValue("Unit", AttributeValueTypesDto.Enum, c.AmountUnit);

                    var entity = new RtEntity
                    {
                        CkTypeId = emCkType,
                        RtId = anchor.Em.RtId,
                        RtWellKnownName = anchor.Em.RtWellKnownName,
                        RtChangedDateTime = changed
                    };
                    entity.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, timeRange);
                    entity.SetAttributeValue("Amount", AttributeValueTypesDto.Record, amountRecord);
                    entity.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, anchor.ObisCode);
                    entity.SetAttributeValue("DataQuality", AttributeValueTypesDto.Enum, c.DataQuality);

                    datapoints.Add(EntityUpdateInfo<RtEntity>.CreateInsert(entity));
                }
            }

            kWhByObis[anchor.ObisCode] = kWhByObis.GetValueOrDefault(anchor.ObisCode) + total;
        }

        var distinctWarnings = warnings.Distinct(StringComparer.Ordinal).ToList();
        foreach (var warning in distinctWarnings)
        {
            nodeContext.Warning(warning);
        }

        nodeContext.Debug(
            $"{NodeLabel}: produced {datapoints.Count} datapoint(s) for {anchors.Count} of {existingEms.Count} anchor(s) across {numDays} day(s).");

        dataContext.Set(c.EntityUpdatesOutputPath, datapoints, DocumentModes.Extend, ValueKinds.Simple,
            TargetValueWriteModes.Overwrite);

        if (!string.IsNullOrWhiteSpace(c.SummaryOutputPath))
        {
            var summary = new SimulateEnergyMeasurementsSummary
            {
                StartDate = startUtc,
                NumDays = numDays,
                AnchorsTotal = existingEms.Count,
                AnchorsSimulated = anchors.Count,
                AnchorsSkipped = new Dictionary<string, int>(skipped),
                DatapointCount = datapoints.Count,
                EnergyKWhByObisCode = kWhByObis.ToDictionary(kv => kv.Key, kv => EnergySimulationMath.Round(kv.Value)),
                Revisions = timeline.Revisions.Select(r => new SimulateEnergyMeasurementsSummaryRevision
                {
                    RtId = r.RtId,
                    IsBase = r.IsBase,
                    IsDefault = r.IsDefault,
                    EffectiveDate = r.IsDefault ? null : r.EffectiveDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture),
                    Preset = r.Preset,
                    Seed = r.Seed,
                    ConsumptionFactor = r.ConsumptionFactor,
                    PvCapacityFactor = r.PvCapacityFactor,
                    PvSpecificYield = r.PvSpecificYield,
                    MemberSpread = r.MemberSpread,
                    DaysApplied = revisionByDay.Count(d => ReferenceEquals(d, r))
                }).ToList(),
                Warnings = distinctWarnings
            };
            dataContext.Set(c.SummaryOutputPath, summary, DocumentModes.Extend, ValueKinds.Simple,
                TargetValueWriteModes.Overwrite);
        }

        await next(dataContext, nodeContext);
    }

    private static List<Anchor> SelectAnchors(SimulateEnergyMeasurementsV2NodeConfiguration c,
        List<RtEntity> existingEms, Dictionary<OctoObjectId, RtEntity> parentByEmRtId,
        RtCkId<CkTypeId> producerCkType, IDictionary<string, int> skipped, ICollection<string> warnings)
    {
        var anchors = new List<Anchor>();
        var unsupportedProducers = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var fallbackProfiles = new SortedDictionary<string, int>(StringComparer.Ordinal);
        var withoutNumber = 0;
        var withoutParent = 0;

        foreach (var em in existingEms)
        {
            var obisCode = em.GetAttributeStringValueOrDefault("ObisCode", string.Empty) ?? string.Empty;
            if (!parentByEmRtId.TryGetValue(em.RtId, out var parent))
            {
                withoutParent++;
                Count(skipped, SkipNoParent);
                continue;
            }

            var isProducer = parent.CkTypeId != null
                             && string.Equals(parent.CkTypeId.FullName, producerCkType.FullName, StringComparison.Ordinal);
            var rawChannel = isProducer ? c.ProductionObisCode : c.ConsumptionObisCode;
            if (!string.Equals(obisCode, rawChannel, StringComparison.Ordinal))
            {
                Count(skipped, SkipNotARawChannel);
                continue;
            }

            if (c.SimulatedOnly && ReadEnum(parent, c.DataSourceAttribute, ["Eda", "Simulated", "SelfReported"]) != c.SimulatedDataSourceValue)
            {
                Count(skipped, SkipNotSimulated);
                continue;
            }

            var number = ReadString(parent, c.MeteringPointNumberAttribute);
            if (string.IsNullOrEmpty(number))
            {
                withoutNumber++;
                number = string.Empty;
            }

            if (isProducer)
            {
                var productionType = ReadEnum(parent, c.ProductionTypeAttribute, ProductionTypeNames);
                if (productionType is not (null or 0 or 1))
                {
                    var name = productionType is >= 0 and < 7
                        ? ProductionTypeNames[productionType.Value]
                        : productionType.Value.ToString(CultureInfo.InvariantCulture);
                    unsupportedProducers[name] = unsupportedProducers.GetValueOrDefault(name) + 1;
                    Count(skipped, SkipUnsupportedProductionType);
                    continue;
                }

                anchors.Add(new Anchor(em, obisCode, number, true,
                    ReadDouble(parent, c.ProductionCapacityAttribute) ?? c.DefaultProducerKWp, null));
            }
            else
            {
                var profileValue = ReadEnum(parent, c.LoadProfileAttribute, ["H0", "G0", "L0", "HeatPump", "Ev"]);
                string profile;
                if (profileValue == null)
                {
                    profile = c.DefaultLoadProfile.ToUpperInvariant();
                }
                else if (profileValue is >= 0 and < 3)
                {
                    profile = LoadProfileByEnum[profileValue.Value];
                }
                else
                {
                    var name = profileValue switch
                    {
                        3 => "HeatPump",
                        4 => "Ev",
                        _ => profileValue.Value.ToString(CultureInfo.InvariantCulture)
                    };
                    fallbackProfiles[name] = fallbackProfiles.GetValueOrDefault(name) + 1;
                    profile = "H0";
                }

                anchors.Add(new Anchor(em, obisCode, number, false,
                    ReadDouble(parent, c.AnnualConsumptionAttribute) ?? c.DefaultConsumerAnnualKWh, profile));
            }
        }

        if (withoutParent > 0)
        {
            warnings.Add(
                $"{NodeLabel}: {withoutParent} anchor(s) have no parent metering point via role '{c.ParentAssociationRoleId}' and were skipped.");
        }

        foreach (var (type, count) in unsupportedProducers)
        {
            warnings.Add(
                $"{NodeLabel}: {count} producer(s) with production type '{type}' skipped; only PV is simulated (the hydro profile follows in M3, AB#5638).");
        }

        foreach (var (profile, count) in fallbackProfiles)
        {
            warnings.Add(
                $"{NodeLabel}: {count} consumer(s) with load profile '{profile}' simulated as H0; this profile is planned for M4.");
        }

        if (withoutNumber > 0)
        {
            warnings.Add(
                $"{NodeLabel}: {withoutNumber} metering point(s) have no '{c.MeteringPointNumberAttribute}'; their member spread is derived from the seed only.");
        }

        return anchors
            .OrderBy(a => a.MeteringPointNumber, StringComparer.Ordinal)
            .ThenBy(a => a.ObisCode, StringComparer.Ordinal)
            .ThenBy(a => a.Em.RtId.ToString(), StringComparer.Ordinal)
            .ToList();
    }

    private static void Count(IDictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var n) ? n + 1 : 1;

    private static object? ReadAttribute(RtEntity entity, string name)
    {
        if (entity.Attributes.TryGetValue(name, out var exact))
        {
            return exact;
        }

        foreach (var (key, value) in entity.Attributes)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return null;
    }

    private static string? ReadString(RtEntity entity, string name) =>
        ReadAttribute(entity, name) switch
        {
            null => null,
            string s => s,
            IFormattable f => f.ToString(null, CultureInfo.InvariantCulture),
            var other => other.ToString()
        };

    private static double? ReadDouble(RtEntity entity, string name) =>
        ReadAttribute(entity, name) switch
        {
            null => null,
            double d => d,
            float f => f,
            int i => i,
            long l => l,
            decimal m => (double)m,
            string s when double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) => parsed,
            _ => null
        };

    /// <summary>
    /// Reads an enum attribute as its integer key. Accepts the stored integer, a numeric string or
    /// the enum value's name (case-insensitive, matched against <paramref name="names"/>).
    /// </summary>
    private static int? ReadEnum(RtEntity entity, string name, IReadOnlyList<string> names)
    {
        var value = ReadAttribute(entity, name);
        switch (value)
        {
            case null:
                return null;
            case int i:
                return i;
            case long l:
                return (int)l;
            case Enum e:
                return Convert.ToInt32(e, CultureInfo.InvariantCulture);
            case IConvertible when value is not string:
                return Convert.ToInt32(value, CultureInfo.InvariantCulture);
            case string s:
                if (int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed))
                {
                    return parsed;
                }

                for (var index = 0; index < names.Count; index++)
                {
                    if (string.Equals(names[index], s, StringComparison.OrdinalIgnoreCase))
                    {
                        return index;
                    }
                }

                return -1;
            default:
                return -1;
        }
    }

    private static DateTime ResolveStartDate(SimulateEnergyMeasurementsV2NodeConfiguration c, IDataContext dataContext)
    {
        if (!string.IsNullOrWhiteSpace(c.StartDateAttributePath))
        {
            return dataContext.Get<DateTime>(c.StartDateAttributePath);
        }

        return c.StartDate ?? throw new InvalidOperationException(
            $"{NodeLabel}: neither StartDate nor StartDateAttributePath resolves a value.");
    }

    private static int ResolveNumDays(SimulateEnergyMeasurementsV2NodeConfiguration c, IDataContext dataContext)
    {
        var numDays = !string.IsNullOrWhiteSpace(c.NumDaysAttributePath)
            ? dataContext.Get<int>(c.NumDaysAttributePath)
            : c.NumDays ?? throw new InvalidOperationException(
                $"{NodeLabel}: neither NumDays nor NumDaysAttributePath resolves a value.");

        if (numDays <= 0)
        {
            throw new InvalidOperationException($"{NodeLabel}: NumDays must be > 0 (got {numDays}).");
        }

        return numDays;
    }

    private sealed record Anchor(
        RtEntity Em,
        string ObisCode,
        string MeteringPointNumber,
        bool IsProducer,
        double Magnitude,
        string? LoadProfile);
}

/// <summary>Summary written by <c>SimulateEnergyMeasurements@2</c> to its <c>SummaryOutputPath</c>.</summary>
internal sealed record SimulateEnergyMeasurementsSummary
{
    public DateTime StartDate { get; init; }
    public int NumDays { get; init; }
    public int AnchorsTotal { get; init; }
    public int AnchorsSimulated { get; init; }
    public Dictionary<string, int> AnchorsSkipped { get; init; } = new();
    public int DatapointCount { get; init; }
    public Dictionary<string, double> EnergyKWhByObisCode { get; init; } = new();
    public List<SimulateEnergyMeasurementsSummaryRevision> Revisions { get; init; } = [];
    public List<string> Warnings { get; init; } = [];
}

/// <summary>One settings revision as reported in the summary.</summary>
internal sealed record SimulateEnergyMeasurementsSummaryRevision
{
    public string? RtId { get; init; }
    public bool IsBase { get; init; }
    public bool IsDefault { get; init; }
    public string? EffectiveDate { get; init; }
    public string? Preset { get; init; }
    public int Seed { get; init; }
    public double ConsumptionFactor { get; init; }
    public double PvCapacityFactor { get; init; }
    public double PvSpecificYield { get; init; }
    public double MemberSpread { get; init; }
    public int DaysApplied { get; init; }
}
