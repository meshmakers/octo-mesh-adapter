using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.Time.Testing;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// RegisterSelfReportedMeteringPoint@1 (AB#5636, MTR-01): the created chain (customer, facility,
/// metering point, raw anchor, participation period and their associations), idempotent re-register,
/// the conflict codes and the validation that runs before any read or write.
/// </summary>
public class RegisterSelfReportedMeteringPointNodeTests : SessionNodeTestBase
{
    private const string TargetPath = "$.result";
    private const string ConsumerType = "EnergyCommunity/Consumer";
    private const string ProducerType = "EnergyCommunity/Producer";
    private const string CustomerType = "EnergyCommunity/Customer";
    private const string FacilityType = "Basic.Energy/OperatingFacility";
    private const string EmType = "Basic.Energy/EnergyMeasurement";
    private const string PeriodType = "EnergyCommunity/ParticipationPeriod";
    private const string Number = "AT0099980000000010100000000000001";

    private readonly List<RtEntity> _consumers = [];
    private readonly List<RtEntity> _producers = [];
    private readonly List<RtEntity> _customers = [];
    private readonly List<(RtEntity Entity, string MeteringPoint)> _anchors = [];
    private readonly List<(RtEntity Entity, string MeteringPoint)> _periods = [];
    private readonly List<(List<IEntityUpdateInfo<RtEntity>> Entities, List<AssociationUpdateInfo> Associations)> _writes = [];

    public RegisterSelfReportedMeteringPointNodeTests()
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions _, int? _, int? _) =>
                Task.FromResult(ResultSet(type.ToString() switch
                {
                    ConsumerType => _consumers,
                    ProducerType => _producers,
                    CustomerType => _customers,
                    _ => []
                })));

        A.CallTo(() => TenantRepository.GetRtAssociationTargetsAsync(A<IOctoSession>._,
                A<IEnumerable<OctoObjectId>>._, A<RtCkId<CkTypeId>>._, A<RtCkId<CkAssociationRoleId>>._,
                A<RtCkId<CkTypeId>>._, A<GraphDirections>._, A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily(call =>
            {
                var origins = call.GetArgument<IEnumerable<OctoObjectId>>(1)!.Select(x => x.ToString()).ToList();
                var originType = call.GetArgument<RtCkId<CkTypeId>>(2)!;
                var targetType = call.GetArgument<RtCkId<CkTypeId>>(4)!.ToString();
                Assert.Equal(GraphDirections.Inbound, call.GetArgument<GraphDirections>(5));
                var source = targetType == EmType ? _anchors : _periods;
                var pairs = origins.Select(o => new KeyValuePair<RtEntityId, IResultSet<RtEntity>>(
                        new RtEntityId(originType, new OctoObjectId(o)),
                        ResultSet(source.Where(s => s.MeteringPoint == o).Select(s => s.Entity).ToList())))
                    .ToList();
                var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
                A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() => pairs.GetEnumerator());
                return Task.FromResult(multi);
            });

        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> a, OperationResult _) => _writes.Add((e.ToList(), a.ToList())))
            .Returns(Task.CompletedTask);
    }

    private static IResultSet<RtEntity> ResultSet(List<RtEntity> entities)
    {
        var rs = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => rs.Items).Returns(entities.ToList());
        A.CallTo(() => rs.TotalCount).Returns(entities.Count);
        return rs;
    }

    private async Task<JsonObject> RunAsync(JsonNode? body, DateTime? now = null,
        RegisterSelfReportedMeteringPointNodeConfiguration? config = null)
    {
        config ??= new RegisterSelfReportedMeteringPointNodeConfiguration { TargetPath = TargetPath };
        var (dataContext, nodeContext, next, _) = PrepareTestWithLogger(config);
        A.CallTo(() => dataContext.Get<JsonNode>(config.RequestPath)).Returns(body);
        JsonObject? result = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set) && (string)call.Arguments[0]! == TargetPath)
            .Invokes(call => result = (JsonObject)call.Arguments[1]!);

        var node = new RegisterSelfReportedMeteringPointNode(next, EtlContext)
        {
            Clock = new FakeTimeProvider(new DateTimeOffset(now ?? new DateTime(2026, 11, 20, 12, 0, 0, DateTimeKind.Utc),
                TimeSpan.Zero))
        };
        await node.ProcessObjectAsync(dataContext, nodeContext);
        VerifyNextCalled(next, dataContext, nodeContext);
        return result!;
    }

    private static JsonObject Consumption(string number = Number) => new()
    {
        ["meteringPointNumber"] = number,
        ["direction"] = "consumption",
        ["displayName"] = "Batterie Gruppe 1 - Laden",
        ["customer"] = new JsonObject { ["name"] = "Gruppe 1", ["legalEntityType"] = "Company" },
        ["participationFrom"] = "2026-11-13",
        ["participationFactor"] = 80,
        ["loadProfile"] = "H0",
        ["annualConsumptionKwh"] = 3500
    };

    private static RtEntity Inserted(IEnumerable<IEntityUpdateInfo<RtEntity>> infos, string type)
        => infos.Single(i => i.RtEntity!.CkTypeId!.ToString() == type && i.ModOption == EntityModOptions.Insert).RtEntity!;

    private static object? Attr(RtEntity e, string name) => e.GetAttributeValueOrDefault(name);

    private RtEntity ExistingPoint(string type, string rtId, int? dataSource, int pf = 50)
    {
        var e = new RtEntity(new RtCkId<CkTypeId>(type), new OctoObjectId(rtId));
        e.SetAttributeValue("MeteringPointNumber", AttributeValueTypesDto.String, Number);
        e.SetAttributeValue("PartitionFactor", AttributeValueTypesDto.Int, pf);
        if (dataSource is not null) e.SetAttributeValue("MeteringDataSource", AttributeValueTypesDto.Enum, dataSource.Value);
        (type == ConsumerType ? _consumers : _producers).Add(e);
        return e;
    }

    // ==================================================================== create

    [Fact]
    public async Task NewConsumption_CreatesTheWholeChainInOneTransaction()
    {
        var result = await RunAsync(Consumption());

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.True(result["created"]!.GetValue<bool>());

        // The customer is inserted on its own (auto-increment of CustomerNumber), then the chain.
        Assert.Equal(2, _writes.Count);
        var customer = Assert.Single(_writes[0].Entities).RtEntity!;
        Assert.Empty(_writes[0].Associations);
        Assert.Equal(CustomerType, customer.CkTypeId!.ToString());
        Assert.Equal("self-reported-customer-" + RegisterSelfReportedMeteringPointNode.CustomerKey("Gruppe 1"),
            customer.RtWellKnownName);
        Assert.StartsWith("self-reported-customer-gruppe-1-", customer.RtWellKnownName);
        Assert.Null(Attr(customer, "CustomerNumber"));
        var contact = (RtRecord)Attr(customer, "Contact")!;
        Assert.Equal(2, contact.GetAttributeValueOrDefault("LegalEntityType"));
        Assert.Equal("Gruppe 1", contact.GetAttributeValueOrDefault("CompanyName"));

        var chain = _writes[1];
        Assert.Equal(4, chain.Entities.Count);
        var facility = Inserted(chain.Entities, FacilityType);
        var mp = Inserted(chain.Entities, ConsumerType);
        var anchor = Inserted(chain.Entities, EmType);
        var period = Inserted(chain.Entities, PeriodType);

        Assert.Equal(Number, Attr(mp, "MeteringPointNumber"));
        Assert.Equal("Batterie Gruppe 1 - Laden", Attr(mp, "Name"));
        Assert.Equal(2, Attr(mp, "MeteringDataSource"));
        Assert.Equal(80, Attr(mp, "PartitionFactor"));
        Assert.Equal(1, Attr(mp, "CarrierType"));
        Assert.Equal(1, Attr(mp, "State"));
        Assert.Equal(0, Attr(mp, "LoadProfile"));
        Assert.Equal(3500.0, Attr(mp, "EnergyConsumption"));
        Assert.NotNull(Attr(facility, "Address"));

        Assert.Equal($"{mp.RtId}_1-1:1.9.0 G.01", anchor.RtWellKnownName);
        Assert.Equal("1-1:1.9.0 G.01", Attr(anchor, "ObisCode"));
        Assert.Equal($"{mp.RtId}_participation", period.RtWellKnownName);
        var range = (RtRecord)Attr(period, "TimeRange")!;
        Assert.Equal(new DateTime(2026, 11, 12, 23, 0, 0, DateTimeKind.Utc), range.GetAttributeValueOrDefault("From"));
        Assert.Equal(new DateTime(2099, 12, 31, 23, 59, 59, DateTimeKind.Utc), range.GetAttributeValueOrDefault("To"));

        Assert.Equal(
        [
            (CustomerType, FacilityType, "EnergyCommunity/AssociatedFacilities"),
            (ConsumerType, FacilityType, "System/ParentChild"),
            (EmType, ConsumerType, "System/ParentChild"),
            (PeriodType, ConsumerType, "EnergyCommunity/ParticipationPeriod")
        ], chain.Associations.Select(a => (a.Origin.CkTypeId.ToString(), a.Target.CkTypeId.ToString(), a.RoleId.ToString())));
        Assert.Equal(customer.RtId, chain.Associations[0].Origin.RtId);
        Assert.Equal(anchor.RtId, chain.Associations[2].Origin.RtId);
        Assert.Equal(mp.RtId, chain.Associations[2].Target.RtId);

        var point = result["meteringPoint"]!;
        Assert.Equal(mp.RtId.ToString(), point["rtId"]!.GetValue<string>());
        Assert.Equal(Number, point["meteringPointNumber"]!.GetValue<string>());
        Assert.Equal("consumption", point["direction"]!.GetValue<string>());
        Assert.Equal("1-1:1.9.0 G.01", point["anchors"]![0]!["obisCode"]!.GetValue<string>());
        Assert.Equal(anchor.RtId.ToString(), point["anchors"]![0]!["rtId"]!.GetValue<string>());
        Assert.Equal("2026-11-13", point["participation"]!["from"]!.GetValue<string>());
        Assert.Equal(80, point["participation"]!["factor"]!.GetValue<int>());

        AssertScopedSessionOpened();
        A.CallTo(() => Session.CommitTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ParsedRequest_FromTheApiSketch_IsRegistered()
    {
        var body = JsonNode.Parse("""
            { "meteringPointNumber": "AT0099980000000010100000000000001", "direction": "production",
              "displayName": "Batterie Gruppe 1 - Entladen", "participationFrom": "2026-11-13",
              "participationFactor": 100, "productionType": "Other", "capacityKwp": 10 }
            """);
        var result = await RunAsync(body);

        Assert.True(result["created"]!.GetValue<bool>());
        var mp = Inserted(_writes[^1].Entities, ProducerType);
        Assert.Equal(4, Attr(mp, "ProductionType"));
        Assert.Equal(10.0, Attr(mp, "EnergyProductionCapacity"));
        Assert.Equal("1-1:2.9.0 G.01", result["meteringPoint"]!["anchors"]![0]!["obisCode"]!.GetValue<string>());
    }

    [Fact]
    public async Task ExistingDefaultCustomer_IsReusedAndDefaultsApply()
    {
        var existing = new RtEntity(new RtCkId<CkTypeId>(CustomerType), OctoObjectId.GenerateNewId())
        {
            RtWellKnownName = "self-reported-default-customer"
        };
        _customers.Add(existing);

        var result = await RunAsync(new JsonObject
        {
            ["meteringPointNumber"] = Number,
            ["direction"] = "production"
        }, now: new DateTime(2026, 11, 20, 23, 30, 0, DateTimeKind.Utc));

        var chain = Assert.Single(_writes);
        Assert.Equal(existing.RtId, chain.Associations[0].Origin.RtId);
        var mp = Inserted(chain.Entities, ProducerType);
        Assert.Equal(Number, Attr(mp, "Name"));
        Assert.Equal(100, Attr(mp, "PartitionFactor"));
        Assert.Equal(0, Attr(mp, "ProductionType"));
        Assert.Equal("1-1:2.9.0 G.01", Attr(Inserted(chain.Entities, EmType), "ObisCode"));
        // 23:30Z on 2026-11-20 is already 2026-11-21 in Vienna: "today" is the local date.
        Assert.Equal("2026-11-21", result["meteringPoint"]!["participation"]!["from"]!.GetValue<string>());
        Assert.Equal(100, result["meteringPoint"]!["participation"]!["factor"]!.GetValue<int>());
    }

    [Fact]
    public async Task NaturalPersonCustomer_GetsALastName()
    {
        var body = Consumption();
        body["customer"] = new JsonObject { ["name"] = "Max Muster", ["legalEntityType"] = "NaturalPerson" };
        await RunAsync(body);

        var contact = (RtRecord)Attr(_writes[0].Entities[0].RtEntity!, "Contact")!;
        Assert.Equal(0, contact.GetAttributeValueOrDefault("LegalEntityType"));
        Assert.Equal("Max Muster", contact.GetAttributeValueOrDefault("LastName"));
    }

    // ==================================================================== idempotent update

    [Fact]
    public async Task SameNumberAndDirection_IsNotCreatedAgainAndKeepsTheParticipationStart()
    {
        var mp = ExistingPoint(ConsumerType, "0000000000000000000000c1", dataSource: 2);
        var anchor = new RtEntity(new RtCkId<CkTypeId>(EmType), new OctoObjectId("0000000000000000000000d1"));
        anchor.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, "1-1:1.9.0 G.01");
        var other = new RtEntity(new RtCkId<CkTypeId>(EmType), new OctoObjectId("0000000000000000000000d0"));
        other.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, "1-1:2.9.0 G.03");
        _anchors.Add((anchor, mp.RtId.ToString()));
        _anchors.Add((other, mp.RtId.ToString()));
        var period = new RtEntity(new RtCkId<CkTypeId>(PeriodType), new OctoObjectId("0000000000000000000000e1"));
        var range = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/TimeRange") };
        range.SetAttributeValue("From", AttributeValueTypesDto.DateTime, new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc));
        period.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, range);
        _periods.Add((period, mp.RtId.ToString()));

        var result = await RunAsync(Consumption());

        Assert.Equal("ok", result["status"]!.GetValue<string>());
        Assert.False(result["created"]!.GetValue<bool>());
        var point = result["meteringPoint"]!;
        Assert.Equal(mp.RtId.ToString(), point["rtId"]!.GetValue<string>());
        Assert.Equal("0000000000000000000000d1", point["anchors"]![0]!["rtId"]!.GetValue<string>());
        Assert.Equal("2026-11-01", point["participation"]!["from"]!.GetValue<string>());
        Assert.Equal(80, point["participation"]!["factor"]!.GetValue<int>());

        var write = Assert.Single(_writes);
        Assert.Empty(write.Associations);
        var update = Assert.Single(write.Entities);
        Assert.Equal(EntityModOptions.Update, update.ModOption);
        Assert.Equal(mp.RtId, update.RtId);
        Assert.Equal("Batterie Gruppe 1 - Laden", Attr(update.RtEntity!, "Name"));
        Assert.Equal(80, Attr(update.RtEntity!, "PartitionFactor"));
        Assert.Null(Attr(update.RtEntity!, "MeteringPointNumber"));
    }

    [Fact]
    public async Task SameRequestWithoutChanges_WritesNothingAndReportsTheStoredFactor()
    {
        var mp = ExistingPoint(ConsumerType, "0000000000000000000000c1", dataSource: 2, pf: 60);
        var anchor = new RtEntity(new RtCkId<CkTypeId>(EmType), new OctoObjectId("0000000000000000000000d1"));
        anchor.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, "1-1:1.9.0 G.01");
        _anchors.Add((anchor, mp.RtId.ToString()));
        var period = new RtEntity(new RtCkId<CkTypeId>(PeriodType), new OctoObjectId("0000000000000000000000e1"));
        var range = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/TimeRange") };
        range.SetAttributeValue("From", AttributeValueTypesDto.DateTime, new DateTime(2026, 10, 31, 23, 0, 0, DateTimeKind.Utc));
        period.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, range);
        _periods.Add((period, mp.RtId.ToString()));

        var result = await RunAsync(new JsonObject { ["meteringPointNumber"] = Number, ["direction"] = "consumption" });

        Assert.False(result["created"]!.GetValue<bool>());
        Assert.Equal(60, result["meteringPoint"]!["participation"]!["factor"]!.GetValue<int>());
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task ExistingPointWithoutAnchorOrPeriod_IsRepaired()
    {
        var mp = ExistingPoint(ConsumerType, "0000000000000000000000c1", dataSource: 2);
        var result = await RunAsync(new JsonObject
        {
            ["meteringPointNumber"] = Number, ["direction"] = "consumption", ["participationFrom"] = "2026-11-13"
        });

        var write = Assert.Single(_writes);
        Assert.Equal(2, write.Entities.Count);
        Assert.Equal($"{mp.RtId}_1-1:1.9.0 G.01", Inserted(write.Entities, EmType).RtWellKnownName);
        Assert.Equal("2026-11-13", result["meteringPoint"]!["participation"]!["from"]!.GetValue<string>());
    }

    // ==================================================================== conflicts

    [Fact]
    public async Task OtherDirection_IsDirectionConflict()
    {
        ExistingPoint(ProducerType, "0000000000000000000000a1", dataSource: 2);
        var result = await RunAsync(Consumption());

        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal(RegisterSelfReportedMeteringPointNode.DirectionConflict, result["code"]!.GetValue<string>());
        Assert.False(result["created"]!.GetValue<bool>());
        Assert.Empty(_writes);
        A.CallTo(() => Session.AbortTransactionAsync()).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(1)]
    [InlineData(0)]
    [InlineData(null)]
    public async Task SimulatedOrEdaPoint_IsNotSelfReported(int? dataSource)
    {
        ExistingPoint(ConsumerType, "0000000000000000000000c1", dataSource);
        var result = await RunAsync(Consumption());

        Assert.Equal(RegisterSelfReportedMeteringPointNode.NotSelfReported, result["code"]!.GetValue<string>());
        Assert.Empty(_writes);
    }

    // ==================================================================== validation (no I/O)

    [Theory]
    [InlineData("AT0099990000000010100000000000001", "NUMBER_RESERVED")]
    [InlineData("AT0000000000000000000000000000801", "NUMBER_RESERVED")]
    [InlineData("AT009998000000001010000000000001", "VALIDATION")] // 32 characters
    [InlineData("DE0099980000000010100000000000001", "VALIDATION")]
    [InlineData("at0099980000000010100000000000001", "VALIDATION")]
    public async Task Numbers(string number, string code)
    {
        var result = await RunAsync(Consumption(number));
        Assert.Equal(code, result["code"]!.GetValue<string>());
        AssertNoSessionOpened();
    }

    public static TheoryData<string, JsonNode?> InvalidFields => new()
    {
        { "direction", "sideways" },
        { "direction", null },
        { "participationFactor", 0 },
        { "participationFactor", 101 },
        { "participationFactor", 50.5 },
        { "participationFactor", "80" },
        { "participationFrom", "13.11.2026" },
        { "loadProfile", "X1" },
        { "annualConsumptionKwh", -1 },
        { "displayName", "" },
        { "customer", "Gruppe 1" },
        { "customer", new JsonObject { ["legalEntityType"] = "Company" } },
        { "customer", new JsonObject { ["name"] = "G", ["legalEntityType"] = "Robot" } },
        { "productionType", "Solar" },
        { "capacityKwp", 10 }
    };

    [Theory]
    [MemberData(nameof(InvalidFields))]
    public async Task InvalidField_IsValidationBeforeAnyRead(string field, JsonNode? value)
    {
        var body = Consumption();
        if (field == "direction" && value is null)
        {
            body.Remove("direction");
        }
        else
        {
            body[field] = value?.DeepClone();
        }

        var result = await RunAsync(body);

        Assert.Equal("error", result["status"]!.GetValue<string>());
        Assert.Equal(RegisterSelfReportedMeteringPointNode.Validation, result["code"]!.GetValue<string>());
        AssertNoSessionOpened();
        Assert.Empty(_writes);
    }

    [Fact]
    public async Task NotAnObject_IsValidation()
    {
        var result = await RunAsync(new JsonArray());
        Assert.Equal(RegisterSelfReportedMeteringPointNode.Validation, result["code"]!.GetValue<string>());
    }

    [Fact]
    public void CustomerKey_IsStableAndCaseInsensitive()
    {
        Assert.Equal(RegisterSelfReportedMeteringPointNode.CustomerKey("Gruppe 1"),
            RegisterSelfReportedMeteringPointNode.CustomerKey("  gruppe 1 "));
        Assert.NotEqual(RegisterSelfReportedMeteringPointNode.CustomerKey("Gruppe 1"),
            RegisterSelfReportedMeteringPointNode.CustomerKey("Gruppe-1"));
    }
}
