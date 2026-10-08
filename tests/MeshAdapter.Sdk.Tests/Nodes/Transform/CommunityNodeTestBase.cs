using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Runtime.Engine.CrateDb;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// The faked community world shared by the tests of CommunityEnergyBalance@1 and
/// PrepareMeterReadings@1 (AB#5637, AB#5639): consumers, producers, participation periods, raw
/// anchors and archive rows, served through the same repository calls AllocateCommunityEnergy@1
/// makes. Mirrors the world of <see cref="AllocateCommunityEnergyNodeTests"/>.
/// </summary>
public abstract class CommunityNodeTestBase : SessionNodeTestBase
{
    protected const string TenantId = "test-tenant";
    protected const string ConsumerType = "EnergyCommunity/Consumer";
    protected const string ProducerType = "EnergyCommunity/Producer";
    protected const string PeriodType = "EnergyCommunity/ParticipationPeriod";
    protected const string EmType = "Basic.Energy/EnergyMeasurement";
    protected const string ConsumerIn = "1-1:1.9.0 G.01";
    protected const string ProducerIn = "1-1:2.9.0 G.01";
    protected const string ShareObis = "1-1:2.9.0 G.03";
    protected const string OfferedObis = "1-1:2.9.0 G.01T";
    protected const string SurplusObis = "1-1:2.9.0 P.01T";

    protected static readonly OctoObjectId ArchiveRtId = new("ec0000000000000000000a01");

    /// <summary>Day under test: 2026-11-15 in Vienna = [2026-11-14T23:00Z, 2026-11-15T23:00Z).</summary>
    protected static readonly DateOnly Day = new(2026, 11, 15);
    protected static readonly DateTime DayStartUtc = new(2026, 11, 14, 23, 0, 0, DateTimeKind.Utc);
    protected static readonly DateTime Open = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    protected readonly ISystemContext SystemContext = A.Fake<ISystemContext>();
    private readonly IStreamDataRepository _streamData = A.Fake<IStreamDataRepository>();
    private readonly ArchiveSnapshot _snapshot;

    protected readonly List<RtEntity> Consumers = [];
    protected readonly List<RtEntity> Producers = [];
    protected readonly List<(RtEntity Period, string MeteringPoint)> Periods = [];
    protected readonly List<(RtEntity Anchor, string MeteringPoint)> Anchors = [];
    protected readonly List<(string Anchor, DateTime From, DateTime To, object? Value, object? Quality)> Rows = [];
    protected readonly List<StreamDataQueryOptions> Queries = [];
    protected readonly List<RtEntityQueryOptions> PointQueries = [];

    protected CommunityNodeTestBase()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(TenantId);
        var tenantContext = A.Fake<ITenantContext>();
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => SystemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(tenantContext));
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_streamData);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(archiveStore);

        _snapshot = new ArchiveSnapshot(ArchiveRtId, new RtCkId<CkTypeId>(EmType), CkArchiveStatus.Activated,
            "energy", [new CkArchiveColumnSpec("Amount.Value", false, false), new CkArchiveColumnSpec("DataQuality", false, false)])
        {
            IsTimeRange = true
        };
        A.CallTo(() => archiveStore.GetAsync(ArchiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(_snapshot));

        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily((IOctoSession _, RtCkId<CkTypeId> type, RtEntityQueryOptions options, int? _, int? _) =>
            {
                if (type.ToString() is ConsumerType or ProducerType)
                {
                    PointQueries.Add(options);
                }

                return Task.FromResult(ResultSet(type.ToString() switch
                {
                    ConsumerType => Consumers,
                    ProducerType => Producers,
                    PeriodType => Periods.Select(p => p.Period).ToList(),
                    EmType => Anchors.Select(a => a.Anchor).ToList(),
                    _ => []
                }));
            });

        A.CallTo(() => TenantRepository.GetRtAssociationTargetsAsync(A<IOctoSession>._,
                A<IEnumerable<OctoObjectId>>._, A<RtCkId<CkTypeId>>._, A<RtCkId<CkAssociationRoleId>>._,
                A<RtCkId<CkTypeId>>._, A<GraphDirections>._, A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ReturnsLazily(call =>
            {
                var originType = call.GetArgument<RtCkId<CkTypeId>>(2)!;
                var links = originType.ToString() == PeriodType
                    ? Periods.Select(p => (p.Period, p.MeteringPoint))
                    : Anchors.Select(a => (a.Anchor, a.MeteringPoint));
                return Task.FromResult(MultiResult(originType, links));
            });

        A.CallTo(() => _streamData.ExecuteQueryAsync(A<OctoObjectId>._, A<StreamDataQueryOptions>._))
            .ReturnsLazily((OctoObjectId _, StreamDataQueryOptions options) => Task.FromResult(Query(options)));
    }

    // ==================================================================== world builders

    protected static IResultSet<RtEntity> ResultSet(List<RtEntity> entities)
    {
        var rs = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => rs.Items).Returns(entities.ToList());
        A.CallTo(() => rs.TotalCount).Returns(entities.Count);
        return rs;
    }

    private static IMultipleOriginResultSet<RtEntity> MultiResult(RtCkId<CkTypeId> originType,
        IEnumerable<(RtEntity Origin, string Target)> links)
    {
        var pairs = links
            .GroupBy(l => l.Origin.RtId)
            .Select(g => new KeyValuePair<RtEntityId, IResultSet<RtEntity>>(
                new RtEntityId(originType, g.Key),
                ResultSet(g.Select(l => new RtEntity(new RtCkId<CkTypeId>("Basic.Energy/MeteringPoint"),
                    new OctoObjectId(l.Target))).ToList())))
            .ToList();
        var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() => pairs.GetEnumerator());
        return multi;
    }

    private static RtEntity Point(string type, string rtId, string number, int? pf, int? dataSource)
    {
        var e = new RtEntity(new RtCkId<CkTypeId>(type), new OctoObjectId(rtId));
        e.SetAttributeValue("MeteringPointNumber", AttributeValueTypesDto.String, number);
        if (pf is not null) e.SetAttributeValue("PartitionFactor", AttributeValueTypesDto.Int, pf.Value);
        if (dataSource is not null) e.SetAttributeValue("MeteringDataSource", AttributeValueTypesDto.Enum, dataSource.Value);
        return e;
    }

    private int _sequence;

    private string NextId(string prefix) => $"{prefix}{++_sequence:D20}";

    protected void AddConsumer(string rtId, string number, int? pf = 100, int? dataSource = 1, DateTime? from = null,
        DateTime? to = null, bool participates = true)
    {
        Consumers.Add(Point(ConsumerType, rtId, number, pf, dataSource));
        AddAnchor(rtId, ConsumerIn);
        if (participates) AddPeriod(rtId, from ?? Open, to);
    }

    protected void AddProducer(string rtId, string number, int? pf = 100, int? dataSource = 1, DateTime? from = null,
        DateTime? to = null)
    {
        Producers.Add(Point(ProducerType, rtId, number, pf, dataSource));
        AddAnchor(rtId, ProducerIn);
        AddPeriod(rtId, from ?? Open, to);
    }

    protected void AddPeriod(string meteringPoint, DateTime from, DateTime? to)
    {
        var period = new RtEntity(new RtCkId<CkTypeId>(PeriodType), new OctoObjectId(NextId("eeee")));
        var range = new RtRecord { CkRecordId = new RtCkId<CkRecordId>("Basic/TimeRange") };
        range.SetAttributeValue("From", AttributeValueTypesDto.DateTime, from);
        if (to is not null) range.SetAttributeValue("To", AttributeValueTypesDto.DateTime, to.Value);
        period.SetAttributeValue("TimeRange", AttributeValueTypesDto.Record, range);
        Periods.Add((period, meteringPoint));
    }

    private void AddAnchor(string meteringPoint, string obis)
    {
        var anchor = new RtEntity(new RtCkId<CkTypeId>(EmType), new OctoObjectId(NextId("dddd")));
        anchor.SetAttributeValue("ObisCode", AttributeValueTypesDto.String, obis);
        Anchors.Add((anchor, meteringPoint));
    }

    protected string AnchorOf(string meteringPoint, string obis)
        => Anchors.Single(a => a.MeteringPoint == meteringPoint
                               && a.Anchor.GetAttributeStringValueOrDefault("ObisCode") == obis).Anchor.RtId.ToString();

    /// <summary>One row per slot of <paramref name="days"/> days from <see cref="DayStartUtc"/>.</summary>
    protected void Fill(string meteringPoint, string obis, Func<int, object?> value, int days = 1,
        Func<int, int>? quality = null)
    {
        var anchor = AnchorOf(meteringPoint, obis);
        var start = DayStartUtc;
        var end = DayStartUtc.AddDays(days);
        var i = 0;
        for (var t = start; t < end; t = t.AddMinutes(15), i++)
        {
            var v = value(i);
            if (v is null)
            {
                continue;
            }

            AddRow(anchor, t, v, quality?.Invoke(i) ?? 1);
        }
    }

    protected void AddRow(string anchor, DateTime from, object value, int quality)
        => Rows.Add((anchor, from, from.AddMinutes(15), value, quality));

    /// <summary>The faked archive: honours rtIds, time window overlap, sort (fixed) and paging.</summary>
    private StreamDataQueryResult Query(StreamDataQueryOptions options)
    {
        Queries.Add(options);
        var resolver = StreamDataFieldResolver.CreateForArchive(_snapshot);
        string Key(string name) => resolver.Resolve(name)!.CrateDbName;

        var ids = options.RtIds?.Select(x => x.ToString()).ToHashSet() ?? [];
        var matching = Rows
            .Where(r => ids.Contains(r.Anchor))
            .Where(r => (options.To is null || r.From < options.To) && (options.From is null || r.To > options.From))
            .OrderBy(r => r.From).ThenBy(r => r.Anchor, StringComparer.Ordinal)
            .ToList();

        var page = matching.Skip(options.Offset ?? 0).Take(options.PageSize ?? int.MaxValue)
            .Select(r => new StreamDataRow
            {
                RtId = new OctoObjectId(r.Anchor),
                Timestamp = r.To,
                Values = new Dictionary<string, object?>
                {
                    [Key("window_start")] = r.From,
                    [Key("window_end")] = r.To,
                    [Key("Amount.Value")] = r.Value,
                    [Key("DataQuality")] = r.Quality
                }
            })
            .ToList();

        return new StreamDataQueryResult { Rows = page, TotalCount = matching.Count };
    }
}
