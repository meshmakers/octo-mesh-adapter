using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.SimulationNodes.Generators;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins SimulateEnergyMeasurements@2 (AB#5631, SIM-01..04, SIM-08/09): per-metering-point sizing,
/// calibrated PV, raw channels only, simulated metering points only, seeded randomness that never
/// depends on an rtId, and the settings revision timeline.
/// </summary>
public class SimulateEnergyMeasurementsV2NodeTests : SessionNodeTestBase
{
    private const string EmCkTypeId = "Basic.Energy/EnergyMeasurement";
    private const string MeteringPointCkTypeId = "Basic.Energy/MeteringPoint";
    private const string ProducerCkTypeId = "EnergyCommunity/Producer";
    private const string ConsumerCkTypeId = "EnergyCommunity/Consumer";
    private const string SettingsCkTypeId = "EnergyCommunity/SimulationSettings";
    private const string RoleId = "System/ParentChild";
    private const string OutputPath = "$.datapoints";
    private const string SummaryPath = "$.summary";
    private const string SettingsPath = "$.settings.Items";

    private const string ConsumptionObis = "1-1:1.9.0 G.01";
    private const string ProductionObis = "1-1:2.9.0 G.01";

    private const int Simulated = 1;

    private static readonly DateTime Jan1st2025 = new(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private int _nextRtId = 1;

    // ---------------------------------------------------------------------------------------------
    // Fixture helpers
    // ---------------------------------------------------------------------------------------------

    private sealed record Member(RtEntity Em, RtEntity Parent);

    private OctoObjectId NewRtId(int offset = 0) => new((_nextRtId++ + offset).ToString("x24"));

    private Member Consumer(string number, double? annualKWh = 3500, int? dataSource = Simulated,
        int? loadProfile = 0, string obis = ConsumptionObis, int rtIdOffset = 0) =>
        CreateMember(ConsumerCkTypeId, number, obis, rtIdOffset, parent =>
        {
            if (annualKWh != null) parent.SetAttributeValue("EnergyConsumption", AttributeValueTypesDto.Double, annualKWh.Value);
            if (dataSource != null) parent.SetAttributeValue("MeteringDataSource", AttributeValueTypesDto.Enum, dataSource.Value);
            if (loadProfile != null) parent.SetAttributeValue("LoadProfile", AttributeValueTypesDto.Enum, loadProfile.Value);
        });

    private Member Producer(string number, double? kWp = 1, int? dataSource = Simulated, int? productionType = 1,
        string obis = ProductionObis, int rtIdOffset = 0) =>
        CreateMember(ProducerCkTypeId, number, obis, rtIdOffset, parent =>
        {
            if (kWp != null) parent.SetAttributeValue("EnergyProductionCapacity", AttributeValueTypesDto.Double, kWp.Value);
            if (dataSource != null) parent.SetAttributeValue("MeteringDataSource", AttributeValueTypesDto.Enum, dataSource.Value);
            if (productionType != null) parent.SetAttributeValue("ProductionType", AttributeValueTypesDto.Enum, productionType.Value);
        });

    private Member CreateMember(string parentCkTypeId, string number, string obis, int rtIdOffset,
        Action<RtEntity> configureParent)
    {
        var em = new RtEntity(new RtCkId<CkTypeId>(EmCkTypeId), NewRtId(rtIdOffset))
        {
            RtWellKnownName = $"{number}-{obis}"
        };
        em.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, obis);

        var parent = new RtEntity(new RtCkId<CkTypeId>(parentCkTypeId), NewRtId(rtIdOffset));
        parent.SetAttributeValue("MeteringPointNumber", AttributeValueTypesDto.String, number);
        configureParent(parent);
        return new Member(em, parent);
    }

    private static IResultSet<RtEntity> ResultSet(IEnumerable<RtEntity> entities)
    {
        var list = entities.ToList();
        return new ResultSet<RtEntity>(list, list.Count, null, null);
    }

    private void SetupMembers(params Member[] members) => SetupMembers(members, withoutParent: []);

    private void SetupMembers(IReadOnlyList<Member> members, IReadOnlyList<RtEntity> withoutParent)
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>.That.Matches(t => t.ToString() == EmCkTypeId),
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .ReturnsLazily(() => Task.FromResult(ResultSet(members.Select(m => m.Em).Concat(withoutParent))));

        var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() => members
            .Select(m => new KeyValuePair<RtEntityId, IResultSet<RtEntity>>(
                new RtEntityId(new RtCkId<CkTypeId>(EmCkTypeId), m.Em.RtId), ResultSet([m.Parent])))
            .GetEnumerator());

        A.CallTo(() => TenantRepository.GetRtAssociationTargetsAsync(
                Session,
                A<IEnumerable<OctoObjectId>>._,
                A<RtCkId<CkTypeId>>._,
                A<RtCkId<CkAssociationRoleId>>._,
                A<RtCkId<CkTypeId>>._,
                A<GraphDirections>._,
                A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(Task.FromResult(multi));
    }

    private static SimulateEnergyMeasurementsV2NodeConfiguration CreateConfig(DateTime start, int numDays,
        string? settingsPath = null) => new()
    {
        StartDate = start,
        NumDays = numDays,
        EnergyMeasurementCkTypeId = EmCkTypeId,
        TimeRangeCkRecordId = "Basic/TimeRange",
        AmountCkRecordId = "Basic/Amount",
        ParentAssociationRoleId = RoleId,
        MeteringPointCkTypeId = MeteringPointCkTypeId,
        ProducerCkTypeId = ProducerCkTypeId,
        EntityUpdatesOutputPath = OutputPath,
        SummaryOutputPath = SummaryPath,
        SettingsPath = settingsPath
    };

    private sealed record RunResult(List<EntityUpdateInfo<RtEntity>> Datapoints, SimulateEnergyMeasurementsSummary Summary);

    private async Task<RunResult> RunAsync(SimulateEnergyMeasurementsV2NodeConfiguration config,
        JsonNode? settings = null)
    {
        var (dataContext, nodeContext, next) = PrepareTest(config);
        if (config.SettingsPath != null)
        {
            SetupGetSimpleValueByPath(dataContext, config.SettingsPath, settings);
        }

        List<EntityUpdateInfo<RtEntity>>? datapoints = null;
        SimulateEnergyMeasurementsSummary? summary = null;
        A.CallTo(() => dataContext.Set(OutputPath, A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes(call => datapoints = call.Arguments.Get<List<EntityUpdateInfo<RtEntity>>>(1));
        A.CallTo(() => dataContext.Set(SummaryPath, A<SimulateEnergyMeasurementsSummary>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes(call => summary = call.Arguments.Get<SimulateEnergyMeasurementsSummary>(1));

        await new SimulateEnergyMeasurementsV2Node(next, EtlContext).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(datapoints);
        Assert.NotNull(summary);
        return new RunResult(datapoints!, summary!);
    }

    private static JsonObject Revision(string rtId, DateTime effectiveFrom, DateTime created,
        double consumptionFactor = 1.0, int seed = 1, int preset = 0, bool superseded = false,
        double memberSpread = 0) =>
        new()
        {
            ["RtId"] = rtId,
            ["RtCreationDateTime"] = created,
            ["CkTypeId"] = SettingsCkTypeId,
            ["Attributes"] = new JsonObject
            {
                ["Preset"] = preset,
                ["EffectiveFrom"] = effectiveFrom,
                ["Seed"] = seed,
                ["ConsumptionFactor"] = consumptionFactor,
                ["MemberSpread"] = memberSpread,
                ["SupersededByReset"] = superseded
            }
        };

    private static double Amount(EntityUpdateInfo<RtEntity> info) =>
        Convert.ToDouble(((RtRecord)info.RtEntity!.Attributes["Amount"]!).Attributes["Value"]);

    private static DateTime From(EntityUpdateInfo<RtEntity> info) =>
        (DateTime)((RtRecord)info.RtEntity!.Attributes["TimeRange"]!).Attributes["From"]!;

    private static string Obis(EntityUpdateInfo<RtEntity> info) =>
        (string)info.RtEntity!.Attributes["ObisCode"]!;

    private static Dictionary<DateOnly, double> DailySums(IEnumerable<EntityUpdateInfo<RtEntity>> points) =>
        points.GroupBy(p => DateOnly.FromDateTime(From(p))).ToDictionary(g => g.Key, g => g.Sum(Amount));

    // ---------------------------------------------------------------------------------------------
    // (1) SIM-01: H0 consumer 3,500 kWh/a over 365 days
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task H0Consumer_3500KWhPerYear_SumsTo3500OverAYear()
    {
        var consumer = Consumer("AT0099990000000011000000000000001", annualKWh: 3500);
        SetupMembers(consumer);

        var result = await RunAsync(CreateConfig(Jan1st2025, 365));

        Assert.Equal(365 * 96, result.Datapoints.Count);
        Assert.All(result.Datapoints, p => Assert.Equal(EntityModOptions.Insert, p.ModOption));
        Assert.All(result.Datapoints, p => Assert.Equal(consumer.Em.RtId, p.RtEntity!.RtId));
        Assert.All(result.Datapoints, p => Assert.Equal(consumer.Em.RtWellKnownName, p.RtEntity!.RtWellKnownName));
        Assert.All(result.Datapoints, p => Assert.Equal(ConsumptionObis, Obis(p)));
        var total = result.Datapoints.Sum(Amount);
        Assert.InRange(total, 3500 * 0.995, 3500 * 1.005);
        Assert.Equal(1, result.Summary.AnchorsSimulated);
        Assert.Equal(365 * 96, result.Summary.DatapointCount);
        Assert.InRange(result.Summary.EnergyKWhByObisCode[ConsumptionObis], 3500 * 0.995, 3500 * 1.005);
    }

    // ---------------------------------------------------------------------------------------------
    // (2) SIM-02: calibrated PV, 1 kWp, peak at 11:00 or 11:15 UTC
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task Pv1KWp_YieldsTheSpecificYieldOverAYear_AndPeaksAt1100Or1115Utc()
    {
        SetupMembers(Producer("AT0099990000000012000000000000001", kWp: 1));

        var result = await RunAsync(CreateConfig(Jan1st2025, 365));

        Assert.Equal(365 * 96, result.Datapoints.Count);
        Assert.All(result.Datapoints, p => Assert.Equal(ProductionObis, Obis(p)));
        Assert.InRange(result.Datapoints.Sum(Amount), 1050 * 0.99, 1050 * 1.01);

        foreach (var day in result.Datapoints.GroupBy(p => DateOnly.FromDateTime(From(p))))
        {
            var peak = day.MaxBy(Amount)!;
            var peakSlot = (int)(From(peak).TimeOfDay.TotalMinutes / 15);
            Assert.True(peakSlot is 44 or 45, $"{day.Key}: peak in slot {peakSlot}");
        }

        // Nights stay dark.
        Assert.All(result.Datapoints.Where(p => From(p).Hour is < 3 or >= 21), p => Assert.Equal(0, Amount(p)));
    }

    // ---------------------------------------------------------------------------------------------
    // (3) G0 and L0 via LoadProfile
    // ---------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(0, "H0")]
    [InlineData(1, "G0")]
    [InlineData(2, "L0")]
    public async Task LoadProfileAttribute_SelectsTheProfile(int loadProfile, string expectedProfile)
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001", annualKWh: 36500, loadProfile: loadProfile));

        var result = await RunAsync(CreateConfig(Jan1st2025, 1));

        var weights = EnergyProfiles.LoadProfileWeights[expectedProfile];
        var ordered = result.Datapoints.OrderBy(From).ToList();
        Assert.Equal(96, ordered.Count);
        for (var slot = 0; slot < 96; slot++)
        {
            Assert.Equal(Math.Round(weights[slot] * 100.0, 6), Amount(ordered[slot]), 9);
        }
    }

    [Fact]
    public async Task HeatPumpProfile_FallsBackToH0WithAWarning()
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001", annualKWh: 36500, loadProfile: 3));

        var result = await RunAsync(CreateConfig(Jan1st2025, 1));

        var h0 = EnergyProfiles.LoadProfileWeights["H0"];
        var ordered = result.Datapoints.OrderBy(From).ToList();
        Assert.Equal(Math.Round(h0[75] * 100.0, 6), Amount(ordered[75]), 9);
        Assert.Contains(result.Summary.Warnings, w => w.Contains("HeatPump") && w.Contains("H0"));
    }

    // ---------------------------------------------------------------------------------------------
    // (4) SIM-04: only Simulated metering points
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task OnlySimulatedMeteringPoints_ProduceOutput()
    {
        var simulated = Consumer("AT0099990000000011000000000000001", dataSource: 1);
        var eda = Consumer("AT0099990000000011000000000000002", dataSource: 0);
        var selfReported = Consumer("AT0099980000000011000000000000003", dataSource: 2);
        var unmarked = Consumer("AT0099990000000011000000000000004", dataSource: null);
        var edaProducer = Producer("AT0099990000000012000000000000001", dataSource: 0);
        SetupMembers(simulated, eda, selfReported, unmarked, edaProducer);

        var result = await RunAsync(CreateConfig(Jan1st2025, 2));

        Assert.All(result.Datapoints, p => Assert.Equal(simulated.Em.RtId, p.RtEntity!.RtId));
        Assert.Equal(2 * 96, result.Datapoints.Count);
        Assert.Equal(4, result.Summary.AnchorsSkipped[SimulateEnergyMeasurementsV2Node.SkipNotSimulated]);
        Assert.Equal(5, result.Summary.AnchorsTotal);
    }

    [Fact]
    public async Task SimulatedOnlyOff_SimulatesEveryMeteringPoint()
    {
        SetupMembers(
            Consumer("AT0099990000000011000000000000001", dataSource: 0),
            Consumer("AT0099990000000011000000000000002", dataSource: null));
        var config = CreateConfig(Jan1st2025, 1) with { SimulatedOnly = false };

        var result = await RunAsync(config);

        Assert.Equal(2, result.Summary.AnchorsSimulated);
    }

    // ---------------------------------------------------------------------------------------------
    // (5) SIM-01: raw channels only, never allocation registers
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NonRawObisAnchors_ProduceNothing()
    {
        const string consumerNumber = "AT0099990000000011000000000000001";
        const string producerNumber = "AT0099990000000012000000000000001";
        var raw = new[] { Consumer(consumerNumber), Producer(producerNumber) };
        var allocation = new[]
        {
            Consumer(consumerNumber, obis: "1-1:1.9.0 P.01"),
            Consumer(consumerNumber, obis: "1-1:2.9.0 G.03"),
            Producer(producerNumber, obis: "1-1:2.9.0 G.03"),
            Producer(producerNumber, obis: "1-1:2.9.0 G.01T"),
            Producer(producerNumber, obis: "1-1:2.9.0 P.01T"),
            // A producer channel under a consumer is not a raw consumer channel either.
            Consumer(consumerNumber, obis: ProductionObis)
        };
        SetupMembers(raw.Concat(allocation).ToArray());

        var result = await RunAsync(CreateConfig(Jan1st2025, 1));

        Assert.Equal(new[] { ConsumptionObis, ProductionObis },
            result.Datapoints.Select(Obis).Distinct().OrderBy(o => o, StringComparer.Ordinal));
        Assert.Equal(2 * 96, result.Datapoints.Count);
        Assert.Equal(6, result.Summary.AnchorsSkipped[SimulateEnergyMeasurementsV2Node.SkipNotARawChannel]);
    }

    // ---------------------------------------------------------------------------------------------
    // (6) SIM-03: randomness from (seed, metering point number), never from the rtId
    // ---------------------------------------------------------------------------------------------

    private async Task<List<double>> RunSpreadAsync(int seed, double spread, int rtIdOffset)
    {
        SetupMembers(
            Consumer("AT0099990000000011000000000000001", rtIdOffset: rtIdOffset),
            Producer("AT0099990000000012000000000000001", kWp: 5, rtIdOffset: rtIdOffset));
        var settings = new JsonArray(Revision("aaaaaaaaaaaaaaaaaaaaaaaa", new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc), seed: seed, memberSpread: spread));

        var result = await RunAsync(CreateConfig(new DateTime(2025, 6, 1, 0, 0, 0, DateTimeKind.Utc), 3, SettingsPath),
            settings);
        return result.Datapoints.Select(Amount).ToList();
    }

    [Fact]
    public async Task SameSeedAndNumber_DifferentRtIds_GiveBitIdenticalValues()
    {
        var first = await RunSpreadAsync(seed: 7, spread: 0.3, rtIdOffset: 0);
        var rebuilt = await RunSpreadAsync(seed: 7, spread: 0.3, rtIdOffset: 5000);

        Assert.Equal(first.Count, rebuilt.Count);
        Assert.True(first.Zip(rebuilt).All(p => BitConverter.DoubleToInt64Bits(p.First) == BitConverter.DoubleToInt64Bits(p.Second)));
    }

    [Fact]
    public async Task DifferentSeedWithSpread_GivesDifferentValues()
    {
        var seven = await RunSpreadAsync(seed: 7, spread: 0.3, rtIdOffset: 0);
        var eight = await RunSpreadAsync(seed: 8, spread: 0.3, rtIdOffset: 0);

        Assert.NotEqual(seven.Sum(), eight.Sum());
    }

    [Fact]
    public async Task ZeroSpread_SeedHasNoEffect()
    {
        var seven = await RunSpreadAsync(seed: 7, spread: 0, rtIdOffset: 0);
        var eight = await RunSpreadAsync(seed: 8, spread: 0, rtIdOffset: 0);

        Assert.Equal(seven, eight);
        Assert.Equal(1.0, EnergySimulationMath.MemberSpreadFactor(42, "AT1", 0));
    }

    [Fact]
    public void MemberSpreadFactor_StaysWithinTheSpread()
    {
        for (var i = 0; i < 200; i++)
        {
            Assert.InRange(EnergySimulationMath.MemberSpreadFactor(3, $"AT{i:D31}", 0.25), 0.75, 1.25);
        }
    }

    // ---------------------------------------------------------------------------------------------
    // (7) SIM-08 / RST-07: revision selection
    // ---------------------------------------------------------------------------------------------

    private static DateTime Utc(int year, int month, int day, int hour = 0, int minute = 0) =>
        new(year, month, day, hour, minute, 0, DateTimeKind.Utc);

    [Fact]
    public async Task RevisionA_FromNov1_RevisionB_FromNov15_SplitTheBackfill()
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001", annualKWh: 3650));
        var settings = new JsonArray(
            Revision("00000000000000000000000a", Utc(2026, 11, 1), Utc(2026, 10, 1), consumptionFactor: 1.0),
            Revision("00000000000000000000000b", Utc(2026, 11, 15), Utc(2026, 10, 2), consumptionFactor: 2.0));

        var result = await RunAsync(CreateConfig(Utc(2026, 11, 1), 30, SettingsPath), settings);

        var daily = DailySums(result.Datapoints);
        Assert.Equal(30, daily.Count);
        for (var day = 1; day <= 30; day++)
        {
            var expected = day < 15 ? 10.0 : 20.0;
            Assert.Equal(expected, daily[new DateOnly(2026, 11, day)], 4);
        }

        Assert.Equal(14, result.Summary.Revisions.Single(r => r.RtId == "00000000000000000000000a").DaysApplied);
        Assert.Equal(16, result.Summary.Revisions.Single(r => r.RtId == "00000000000000000000000b").DaysApplied);
    }

    [Fact]
    public async Task LateRevision_ActsFromItsCreationDate_SupersededIgnored_BaseNotClamped()
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001", annualKWh: 3650));
        var settings = new JsonArray(
            // Base revision, written by a reset AFTER the window it governs: not clamped.
            Revision("00000000000000000000000a", Utc(2026, 10, 25), Utc(2026, 11, 25), consumptionFactor: 1.0),
            Revision("00000000000000000000000b", Utc(2026, 11, 15), Utc(2026, 10, 2), consumptionFactor: 2.0),
            // Created 2026-11-19 23:30 UTC = 2026-11-20 00:30 Europe/Vienna, EffectiveFrom 1 Nov:
            // acts from 20 Nov, never retroactively.
            Revision("00000000000000000000000c", Utc(2026, 11, 1), Utc(2026, 11, 19, 23, 30), consumptionFactor: 3.0),
            // Superseded by a reset: would be the base revision (and apply everywhere) otherwise.
            Revision("00000000000000000000000d", Utc(2026, 10, 1), Utc(2026, 9, 1), consumptionFactor: 5.0,
                superseded: true));

        var result = await RunAsync(CreateConfig(Utc(2026, 11, 1), 30, SettingsPath), settings);

        var daily = DailySums(result.Datapoints);
        for (var day = 1; day <= 30; day++)
        {
            var expected = day switch
            {
                < 15 => 10.0,
                < 20 => 20.0,
                _ => 30.0
            };
            Assert.Equal(expected, daily[new DateOnly(2026, 11, day)], 4);
        }

        Assert.DoesNotContain(result.Summary.Revisions, r => r.RtId == "00000000000000000000000d");
        var c = result.Summary.Revisions.Single(r => r.RtId == "00000000000000000000000c");
        Assert.Equal("2026-11-20", c.EffectiveDate);
        var a = result.Summary.Revisions.Single(r => r.IsBase);
        Assert.Equal("00000000000000000000000a", a.RtId);
        Assert.Equal("2026-10-25", a.EffectiveDate);
    }

    [Fact]
    public async Task DaysBeforeTheBaseRevision_UseTheBaseRevision()
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001", annualKWh: 3650));
        var settings = new JsonArray(
            Revision("00000000000000000000000a", Utc(2026, 11, 10), Utc(2026, 11, 10), consumptionFactor: 2.0));

        var result = await RunAsync(CreateConfig(Utc(2026, 11, 1), 3, SettingsPath), settings);

        Assert.All(DailySums(result.Datapoints).Values, v => Assert.Equal(20.0, v, 4));
    }

    [Fact]
    public async Task PresetChangeWithoutReset_Warns_NoiseAndWeather_Warn()
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001"));
        var b = Revision("00000000000000000000000b", Utc(2026, 11, 2), Utc(2026, 10, 2), preset: 3);
        ((JsonObject)b["Attributes"]!)["Noise"] = 2;
        var settings = new JsonArray(
            Revision("00000000000000000000000a", Utc(2026, 11, 1), Utc(2026, 10, 1), preset: 1), b);

        var result = await RunAsync(CreateConfig(Utc(2026, 11, 1), 3, SettingsPath), settings);

        Assert.Contains(result.Summary.Warnings, w => w.Contains("preset") && w.Contains("reset"));
        Assert.Contains(result.Summary.Warnings, w => w.Contains("Noise and Weather") && w.Contains("M3"));
        // The preset has no other effect: the consumer is simulated either way.
        Assert.Equal(3 * 96, result.Datapoints.Count);
    }

    // ---------------------------------------------------------------------------------------------
    // (8) hydro producer skipped with a warning
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task HydroProducer_IsSkippedWithAWarning()
    {
        SetupMembers(
            Producer("AT0099990000000012000000000000001", productionType: 2),
            Producer("AT0099990000000012000000000000002", productionType: null),
            Producer("AT0099990000000012000000000000003", productionType: 0));

        var result = await RunAsync(CreateConfig(Jan1st2025, 1));

        Assert.Equal(2, result.Summary.AnchorsSimulated);
        Assert.Equal(1, result.Summary.AnchorsSkipped[SimulateEnergyMeasurementsV2Node.SkipUnsupportedProductionType]);
        Assert.Contains(result.Summary.Warnings, w => w.Contains("HEP") && w.Contains("AB#5638"));
    }

    [Fact]
    public async Task AnchorWithoutParent_IsSkippedWithAWarning()
    {
        var orphan = new RtEntity(new RtCkId<CkTypeId>(EmCkTypeId), NewRtId());
        orphan.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, ConsumptionObis);
        SetupMembers([Consumer("AT0099990000000011000000000000001")], [orphan]);

        var result = await RunAsync(CreateConfig(Jan1st2025, 1));

        Assert.DoesNotContain(result.Datapoints, p => p.RtEntity!.RtId == orphan.RtId);
        Assert.Equal(1, result.Summary.AnchorsSkipped[SimulateEnergyMeasurementsV2Node.SkipNoParent]);
        Assert.Contains(result.Summary.Warnings, w => w.Contains("no parent metering point"));
    }

    // ---------------------------------------------------------------------------------------------
    // (9) no settings => defaults
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task NoSettings_UsesTheDefaultsWithAWarning()
    {
        SetupMembers(
            Consumer("AT0099990000000011000000000000001", annualKWh: null),
            Producer("AT0099990000000012000000000000001", kWp: null));

        var result = await RunAsync(CreateConfig(Jan1st2025, 1));

        var consumerDay = result.Datapoints.Where(p => Obis(p) == ConsumptionObis).Sum(Amount);
        Assert.Equal(3500.0 / 365.0, consumerDay, 3);
        var producerDay = result.Datapoints.Where(p => Obis(p) == ProductionObis).Sum(Amount);
        Assert.True(producerDay > 0);
        var revision = Assert.Single(result.Summary.Revisions);
        Assert.True(revision.IsDefault);
        Assert.Equal(1, revision.Seed);
        Assert.Equal(1050, revision.PvSpecificYield);
        Assert.Contains(result.Summary.Warnings, w => w.Contains("no simulation settings"));
    }

    [Fact]
    public async Task FlatSettingsObject_IsParsedCaseInsensitively()
    {
        SetupMembers(Consumer("AT0099990000000011000000000000001", annualKWh: 3650));
        var settings = JsonNode.Parse("""{ "effectiveFrom": "2025-01-01", "SEED": 7, "consumptionFactor": 1.5 }""");

        var result = await RunAsync(CreateConfig(Jan1st2025, 1, "$.settings"), settings);

        Assert.Equal(15.0, result.Datapoints.Sum(Amount), 4);
        Assert.Equal(7, Assert.Single(result.Summary.Revisions).Seed);
    }

    // ---------------------------------------------------------------------------------------------
    // (10) the GetRtEntitiesByType@1 serialization shape, through a real DataContext
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task SettingsFromGetRtEntitiesByType_RoundTripThroughTheRealDataContext()
    {
        var consumer = Consumer("AT0099990000000011000000000000001", annualKWh: 3650);
        SetupMembers(consumer);

        var settingsEntity = new RtEntity(new RtCkId<CkTypeId>(SettingsCkTypeId), new OctoObjectId("0000000000000000000000aa"))
        {
            RtCreationDateTime = Utc(2026, 10, 1)
        };
        settingsEntity.SetAttributeValue("Preset", AttributeValueTypesDto.Enum, 1);
        settingsEntity.SetAttributeValue("EffectiveFrom", AttributeValueTypesDto.DateTime, Utc(2026, 11, 1));
        settingsEntity.SetAttributeValue("Seed", AttributeValueTypesDto.Int, 7);
        settingsEntity.SetAttributeValue("ConsumptionFactor", AttributeValueTypesDto.Double, 1.5);
        settingsEntity.SetAttributeValue("MemberSpread", AttributeValueTypesDto.Double, 0.0);
        settingsEntity.SetAttributeValue("SupersededByReset", AttributeValueTypesDto.Boolean, false);
        var superseded = new RtEntity(new RtCkId<CkTypeId>(SettingsCkTypeId), new OctoObjectId("0000000000000000000000bb"))
        {
            RtCreationDateTime = Utc(2026, 9, 1)
        };
        superseded.SetAttributeValue("EffectiveFrom", AttributeValueTypesDto.DateTime, Utc(2026, 10, 1));
        superseded.SetAttributeValue("ConsumptionFactor", AttributeValueTypesDto.Double, 9.0);
        superseded.SetAttributeValue("SupersededByReset", AttributeValueTypesDto.Boolean, true);

        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>.That.Matches(t => t.ToString() == SettingsCkTypeId),
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily(() => Task.FromResult(ResultSet([settingsEntity, superseded])));

        var dataContext = new DataContextImpl(JsonDocument.Parse("{}"));
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);

        // Exactly the pipeline wiring the blueprint uses.
        var getConfig = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = new RtCkId<CkTypeId>(SettingsCkTypeId),
            TargetPath = "$.settings"
        };
        await new GetRtEntitiesByTypeNode(A.Fake<NodeDelegate>(), EtlContext)
            .ProcessObjectAsync(dataContext, root.RegisterChildNode("GetRtEntitiesByType", 0, getConfig, dataContext));

        var simConfig = CreateConfig(Utc(2026, 11, 1), 2, SettingsPath) with { EntityUpdatesOutputPath = "$.out" };
        var simNext = A.Fake<NodeDelegate>();
        await new SimulateEnergyMeasurementsV2Node(simNext, EtlContext)
            .ProcessObjectAsync(dataContext, root.RegisterChildNode("SimulateEnergyMeasurements", 1, simConfig, dataContext));

        var summary = dataContext.Get<JsonNode>(SummaryPath)!.AsObject();
        var revisions = summary["Revisions"]!.AsArray();
        var revision = Assert.Single(revisions)!;
        Assert.Equal("0000000000000000000000aa", revision["RtId"]!.GetValue<string>());
        Assert.Equal(7, revision["Seed"]!.GetValue<int>());
        Assert.Equal(1.5, revision["ConsumptionFactor"]!.GetValue<double>());
        Assert.Equal("2026-11-01", revision["EffectiveDate"]!.GetValue<string>());
        Assert.Equal("1", revision["Preset"]!.GetValue<string>());
        Assert.Equal(2 * 96, summary["DatapointCount"]!.GetValue<int>());
        Assert.Equal(30.0, summary["EnergyKWhByObisCode"]![ConsumptionObis]!.GetValue<double>(), 4);

        var datapoints = dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$.out");
        Assert.NotNull(datapoints);
        Assert.Equal(2 * 96, datapoints!.Count);
        Assert.All(datapoints, p => Assert.Equal(consumer.Em.RtId, p.RtEntity!.RtId));
    }

    // ---------------------------------------------------------------------------------------------
    // (11) legacy equivalence sanity: 14 x H0 182,500 kWh/a + 3 x PV 132.8 kWp
    // ---------------------------------------------------------------------------------------------

    [Fact]
    public async Task LegacyCommunity_AnnualTotalsMatchThePresetTable()
    {
        var members = new List<Member>();
        for (var i = 1; i <= 14; i++)
        {
            members.Add(Consumer($"AT00300000000000000000000000{i:D5}", annualKWh: 182_500));
        }

        for (var i = 1; i <= 3; i++)
        {
            members.Add(Producer($"AT00300000000000000000000001{i:D4}", kWp: 132.8));
        }

        SetupMembers(members.ToArray());

        // Backfill the year in chunks of at most 31 days, like the pipeline does (SIM-10).
        var consumption = 0.0;
        var production = 0.0;
        var start = Jan1st2025;
        var end = Jan1st2025.AddYears(1);
        while (start < end)
        {
            var days = Math.Min(31, (int)(end - start).TotalDays);
            var result = await RunAsync(CreateConfig(start, days));
            consumption += result.Summary.EnergyKWhByObisCode[ConsumptionObis];
            production += result.Summary.EnergyKWhByObisCode[ProductionObis];
            start = start.AddDays(days);
        }

        Assert.InRange(consumption / 1000.0, 2555 * 0.995, 2555 * 1.005);
        Assert.InRange(production / 1000.0, 418 * 0.99, 418 * 1.01);
    }
}
