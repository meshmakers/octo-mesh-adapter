using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using static MeshAdapter.Sdk.Tests.Helpers.ReplacedCreateTestSupport;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
///     AB#7499 — SaveTimeRangeSeriesInArchive@1 inserts the anchor entities of a new series itself, so it refuses
///     them when their (configurable) type is replaced by a CK method, outside the method's own run; the archive
///     rows are not written then either.
/// </summary>
public class SaveTimeRangeSeriesInArchiveReplacedCreateGuardTests : SessionNodeTestBase
{
    private static readonly OctoObjectId ArchiveRtId = new("ec0000000000000000000b01");
    private static readonly DateTime WindowStart = new(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc);

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IStreamDataRepository _streamData = A.Fake<IStreamDataRepository>();
    private readonly List<IReadOnlyList<IEntityUpdateInfo<RtEntity>>> _writes = [];

    public SaveTimeRangeSeriesInArchiveReplacedCreateGuardTests()
    {
        ConfigureRun(EtlContext, null);

        var tenantContext = A.Fake<ITenantContext>();
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => _systemContext.FindTenantContextAsync(Tenant)).Returns(Task.FromResult(tenantContext));
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_streamData);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(archiveStore);
        A.CallTo(() => archiveStore.GetAsync(ArchiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(
            new ArchiveSnapshot(ArchiveRtId, Rt(ReplacedType), CkArchiveStatus.Activated, "test",
                [new CkArchiveColumnSpec("Temperature", false, false)]) { IsTimeRange = true }));

        var existing = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => existing.Items).Returns(new List<RtEntity>());
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult(existing));
        A.CallTo(() => TenantRepository.ApplyChangesAsync(A<IOctoSession>._,
                A<IReadOnlyList<IEntityUpdateInfo<RtEntity>>>._, A<IReadOnlyList<AssociationUpdateInfo>>._,
                A<OperationResult>._))
            .Invokes((IOctoSession _, IReadOnlyList<IEntityUpdateInfo<RtEntity>> e,
                IReadOnlyList<AssociationUpdateInfo> _, OperationResult _) => _writes.Add(e))
            .Returns(Task.CompletedTask);
    }

    private async Task RunAsync(string ckType, ICkCacheService ckCache)
    {
        var config = new SaveTimeRangeSeriesInArchiveNodeConfiguration
        {
            Path = "$.series",
            ArchiveRtId = ArchiveRtId.ToString(),
            CkTypeId = ckType,
            ValuesProperty = "values",
            WellKnownNameFormat = "{key}",
            FromProperty = "from",
            ToProperty = "to",
            Columns = [new TimeRangeSeriesColumn { Name = "Temperature", ValueProperty = "temperature" }]
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var series = new JsonArray(new JsonObject
        {
            ["key"] = "K1",
            ["values"] = new JsonArray(new JsonObject
            {
                ["from"] = WindowStart.ToString("O"),
                ["to"] = WindowStart.AddHours(1).ToString("O")
            })
        });
        A.CallTo(() => dataContext.Get<JsonArray>("$.series")).Returns(series);

        await new SaveTimeRangeSeriesInArchiveNode(next, EtlContext, _systemContext, ckCache)
            .ProcessObjectAsync(dataContext, nodeContext);
    }

    private bool AnchorInserted => _writes.SelectMany(w => w)
        .Any(u => u.ModOption == EntityModOptions.Insert && u.RtEntity!.CkTypeId == Rt(ReplacedType));

    private int RowInserts => Fake.GetCalls(_streamData).Count(c => c.Method.Name.StartsWith("InsertTimeRange"));

    [Fact]
    public async Task AnchorOfMethodReplacedType_WithoutMarker_IsRefused_AndNothingIsWritten()
    {
        var ex = await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RunAsync(ReplacedType, CkCache()));

        Assert.Equal("GENERIC_CREATE_REPLACED_BY_METHOD", ex.Code);
        Assert.False(AnchorInserted);
        Assert.Equal(0, RowInserts);
    }

    [Fact]
    public async Task AnchorOfMethodReplacedType_InTheMethodsOwnRun_IsWritten()
    {
        ConfigureRun(EtlContext, OwnRunMarker());

        await RunAsync(ReplacedType, CkCache());

        Assert.True(AnchorInserted);
    }

    [Fact]
    public async Task AnchorOfNonFlaggedType_WithoutMarker_IsNotAffected()
    {
        A.CallTo(() => _systemContext.FindTenantContextAsync(Tenant)).Returns(Task.FromResult(
            TenantContextWithArchiveOf(PlainType)));

        await RunAsync(PlainType, CkCache());

        Assert.Contains(_writes.SelectMany(w => w),
            u => u.ModOption == EntityModOptions.Insert && u.RtEntity!.CkTypeId == Rt(PlainType));
    }

    private ITenantContext TenantContextWithArchiveOf(string type)
    {
        var tenantContext = A.Fake<ITenantContext>();
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_streamData);
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(archiveStore);
        A.CallTo(() => archiveStore.GetAsync(ArchiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(
            new ArchiveSnapshot(ArchiveRtId, Rt(type), CkArchiveStatus.Activated, "test",
                [new CkArchiveColumnSpec("Temperature", false, false)]) { IsTimeRange = true }));
        return tenantContext;
    }
}
