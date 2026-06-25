using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the reworked SimulateEnergyMeasurements node: it backfills archive datapoints for EXISTING
/// EnergyMeasurement entities (keyed by their stable rtId), derives the profile family from the
/// parent MeteringPoint type, and emits ONLY datapoints (no entity/association creation).
/// </summary>
public class SimulateEnergyMeasurementsNodeTests : NodeTestBase
{
    private const string EmCkTypeId = "Basic.Energy/EnergyMeasurement";
    private const string MeteringPointCkTypeId = "Basic.Energy/MeteringPoint";
    private const string ProducerCkTypeId = "Basic.Energy/Producer";
    private const string ConsumerCkTypeId = "Basic.Energy/Consumer";
    private const string RoleId = "System/ParentChild";
    private const string OutputPath = "$.datapoints";

    private static readonly OctoObjectId ProducerEmRtId = new("000000000000000000000001");
    private static readonly OctoObjectId ConsumerEmRtId = new("000000000000000000000002");

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public SimulateEnergyMeasurementsNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    private static RtEntity CreateEm(OctoObjectId rtId, string wkn, string obisCode)
    {
        var entity = new RtEntity(new RtCkId<CkTypeId>(EmCkTypeId), rtId)
        {
            RtWellKnownName = wkn
        };
        entity.SetAttributeValue("ObisCode",
            Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects.AttributeValueTypesDto.String,
            obisCode);
        return entity;
    }

    private static IResultSet<RtEntity> ResultSet(params RtEntity[] entities)
    {
        var rs = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => rs.Items).Returns(entities.ToList());
        A.CallTo(() => rs.TotalCount).Returns(entities.Length);
        return rs;
    }

    private void SetupExistingEms(params RtEntity[] ems)
    {
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(Task.FromResult(ResultSet(ems)));
    }

    private void SetupParents(Dictionary<OctoObjectId, string> emRtIdToParentCkTypeId)
    {
        var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        var pairs = new List<KeyValuePair<RtEntityId, IResultSet<RtEntity>>>();
        foreach (var (emRtId, parentCkTypeId) in emRtIdToParentCkTypeId)
        {
            var origin = new RtEntityId(new RtCkId<CkTypeId>(EmCkTypeId), emRtId);
            var parent = new RtEntity(new RtCkId<CkTypeId>(parentCkTypeId),
                new OctoObjectId("0000000000000000000000ff"));
            pairs.Add(new KeyValuePair<RtEntityId, IResultSet<RtEntity>>(origin, ResultSet(parent)));
        }

        A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() => pairs.GetEnumerator());

        A.CallTo(() => _tenantRepository.GetRtAssociationTargetsAsync(
                _session,
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

    private static SimulateEnergyMeasurementsNodeConfiguration CreateConfig(int numDays) => new()
    {
        StartDate = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc),
        NumDays = numDays,
        EnergyMeasurementCkTypeId = EmCkTypeId,
        TimeRangeCkRecordId = "Basic/TimeRange",
        AmountCkRecordId = "Basic/Amount",
        ParentAssociationRoleId = RoleId,
        MeteringPointCkTypeId = MeteringPointCkTypeId,
        ProducerCkTypeId = ProducerCkTypeId,
        EntityUpdatesOutputPath = OutputPath
    };

    [Fact]
    public async Task BackfillsDatapointsOnlyForExistingEms_KeyedByExistingRtId()
    {
        const int numDays = 2;
        var producerEm = CreateEm(ProducerEmRtId, "EM-Producer", "1-0:1.8.0");
        var consumerEm = CreateEm(ConsumerEmRtId, "EM-Consumer", "1-0:2.8.0");
        SetupExistingEms(producerEm, consumerEm);
        SetupParents(new Dictionary<OctoObjectId, string>
        {
            [ProducerEmRtId] = ProducerCkTypeId,
            [ConsumerEmRtId] = ConsumerCkTypeId
        });

        var config = CreateConfig(numDays);
        var (dataContext, nodeContext, next) = PrepareTest<SimulateEnergyMeasurementsNodeConfiguration>(config);

        List<EntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => dataContext.Set(
                OutputPath,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes(call => captured = call.Arguments.Get<List<EntityUpdateInfo<RtEntity>>>(1));

        await new SimulateEnergyMeasurementsNode(next, _etlContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);

        // count == numEms * NumDays * 96
        const int slotsPerDay = 96;
        Assert.Equal(2 * numDays * slotsPerDay, captured!.Count);

        // every datapoint is an INSERT keyed to one of the existing EM rtIds (no minted ids).
        var allRtIds = captured.Select(u => u.RtEntity!.RtId).Distinct().ToList();
        Assert.All(captured, u => Assert.Equal(EntityModOptions.Insert, u.ModOption));
        Assert.Equal(2, allRtIds.Count);
        Assert.Contains(ProducerEmRtId, allRtIds);
        Assert.Contains(ConsumerEmRtId, allRtIds);

        // both EMs produced their full slot set, and the wkn is carried from the existing EM.
        var producerPoints = captured.Where(u => u.RtEntity!.RtId == ProducerEmRtId).ToList();
        var consumerPoints = captured.Where(u => u.RtEntity!.RtId == ConsumerEmRtId).ToList();
        Assert.Equal(numDays * slotsPerDay, producerPoints.Count);
        Assert.Equal(numDays * slotsPerDay, consumerPoints.Count);
        Assert.All(producerPoints, u => Assert.Equal("EM-Producer", u.RtEntity!.RtWellKnownName));
        Assert.All(consumerPoints, u => Assert.Equal("EM-Consumer", u.RtEntity!.RtWellKnownName));
    }

    [Fact]
    public async Task ProducerUsesPvPath_ConsumerUsesLoadPath_BothProduceNonZeroAmounts()
    {
        var producerEm = CreateEm(ProducerEmRtId, "EM-Producer", "1-0:1.8.0");
        var consumerEm = CreateEm(ConsumerEmRtId, "EM-Consumer", "1-0:2.8.0");
        SetupExistingEms(producerEm, consumerEm);
        SetupParents(new Dictionary<OctoObjectId, string>
        {
            [ProducerEmRtId] = ProducerCkTypeId,
            [ConsumerEmRtId] = ConsumerCkTypeId
        });

        var config = CreateConfig(1);
        var (dataContext, nodeContext, next) = PrepareTest<SimulateEnergyMeasurementsNodeConfiguration>(config);

        List<EntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => dataContext.Set(
                OutputPath,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes(call => captured = call.Arguments.Get<List<EntityUpdateInfo<RtEntity>>>(1));

        await new SimulateEnergyMeasurementsNode(next, _etlContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);

        var producerAmounts = captured!.Where(u => u.RtEntity!.RtId == ProducerEmRtId)
            .Select(GetAmountValue).ToList();
        var consumerAmounts = captured.Where(u => u.RtEntity!.RtId == ConsumerEmRtId)
            .Select(GetAmountValue).ToList();

        // Both profiles produced at least one non-zero slot (PV peaks at midday, load profile is
        // always positive), proving the producer took the PV path and the consumer the load path.
        Assert.Contains(producerAmounts, a => a > 0);
        Assert.Contains(consumerAmounts, a => a > 0);

        // PV is zero at night; the load profile is strictly positive for every slot.
        Assert.Contains(producerAmounts, a => a == 0);
        Assert.All(consumerAmounts, a => Assert.True(a > 0));
    }

    private static double GetAmountValue(EntityUpdateInfo<RtEntity> info)
    {
        var amountRecord = (RtRecord)info.RtEntity!.Attributes["Amount"]!;
        return Convert.ToDouble(amountRecord.Attributes["Value"]);
    }

    [Fact]
    public async Task NoExistingEms_EmitsEmptyList_AndDoesNotCreateEntities()
    {
        SetupExistingEms(/* none */);
        SetupParents(new Dictionary<OctoObjectId, string>());

        var config = CreateConfig(3);
        var (dataContext, nodeContext, next) = PrepareTest<SimulateEnergyMeasurementsNodeConfiguration>(config);

        List<EntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(() => dataContext.Set(
                OutputPath,
                A<List<EntityUpdateInfo<RtEntity>>>._,
                A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes(call => captured = call.Arguments.Get<List<EntityUpdateInfo<RtEntity>>>(1));

        await new SimulateEnergyMeasurementsNode(next, _etlContext)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);
        Assert.Empty(captured!);
    }
}
