using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using static MeshAdapter.Sdk.Tests.Helpers.AttributeAccessTestModel;

namespace MeshAdapter.Sdk.Tests.Nodes.Access;

/// <summary>AB#7768 — association targets, persisted queries, backfill and the entity-watch trigger.</summary>
public class NodeAttributeAccessMoreTests : SessionNodeTestBase
{
    private readonly ICkCacheService _cache = Cache();

    public NodeAttributeAccessMoreTests()
    {
        ConfigureRun(EtlContext, null);
    }

    private static RtEntity UserWithSecrets() => Account(("Name", "n"), ("PasswordHash", "secret-hash"));

    [Fact]
    public async Task GetAssociationTargets_DropsHiddenFromTheTargets_AndRefusesAFilterOnHidden()
    {
        var origin = OctoObjectId.GenerateNewId();
        var targets = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => targets.Items).Returns(new List<RtEntity> { UserWithSecrets() });
        A.CallTo(() => targets.TotalCount).Returns(1);
        var multi = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => multi.Count).Returns(1);
        A.CallTo(() => multi.GetEnumerator()).ReturnsLazily(() =>
            new List<KeyValuePair<RtEntityId, IResultSet<RtEntity>>>
                { new(new RtEntityId(PlainType, origin), targets) }.GetEnumerator());
        A.CallTo(() => TenantRepository.GetRtAssociationTargetsAsync(A<IOctoSession>._,
                A<IEnumerable<OctoObjectId>>._, A<RtCkId<CkTypeId>>._, A<RtCkId<CkAssociationRoleId>>._,
                A<RtCkId<CkTypeId>>._, A<GraphDirections>._, A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult(multi));

        GetAssociationTargetsNodeConfiguration Config(string? filterPath) => new()
        {
            OriginRtId = origin,
            OriginCkTypeId = PlainType,
            TargetCkTypeId = AccountType,
            AssociationRoleId = new RtCkId<CkAssociationRoleId>("System/ParentChild"),
            GraphDirection = GraphDirectionsDto.Outbound,
            TargetPath = "$.result",
            FieldFilters = filterPath == null
                ? null
                : [new FieldFilterWithPathDto
                    { AttributePath = filterPath, Operator = FieldFilterOperatorDto.Equals, ComparisonValue = "x" }]
        };

        var (dataContext, nodeContext, next) = PrepareTest(Config(null));
        List<MultipleRtEntityResultDto>? emitted = null;
        A.CallTo(() => dataContext.Set("$.result", A<List<MultipleRtEntityResultDto>>._, A<DocumentModes>._,
                A<ValueKinds>._, A<TargetValueWriteModes>._))
            .Invokes(call => emitted = call.GetArgument<List<MultipleRtEntityResultDto>>(1));
        await new GetAssociationTargetsNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext);

        var entity = Assert.Single(Assert.Single(emitted!).Items);
        Assert.False(entity.Attributes.ContainsKey("PasswordHash"));
        Assert.Equal("n", entity.Attributes["Name"]);

        var (dc2, nc2, next2) = PrepareTest(Config("PasswordHash"));
        await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetAssociationTargetsNode(next2, EtlContext, _cache).ProcessObjectAsync(dc2, nc2));
    }

    [Fact]
    public async Task GetQueryById_AColumnOnAHiddenAttribute_FailsTheNode_BeforeAnyRead()
    {
        var queryRtId = OctoObjectId.GenerateNewId();
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Account",
            Columns = new AttributeStringValueList(["name", "passwordHash"])
        };
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(query));
        var (dataContext, nodeContext, next) = PrepareTest(new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryRtId, TargetPath = "$.queryResult"
        });

        var ex = await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetQueryByIdNode(next, EtlContext, _cache, A.Fake<ISystemContext>())
                .ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal("passwordHash", ex.AttributePath);
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "GetRtEntitiesGraphByTypeAsync").MustNotHaveHappened();
    }

    [Fact]
    public async Task BackfillFromRtEntity_NeverCopiesAHiddenValueIntoTheUpdate()
    {
        const string tenant = ReplacedCreateTestSupport.Tenant;
        var archiveRtId = new OctoObjectId("65d5c447b420da3fb12381bc");
        var systemContext = A.Fake<ISystemContext>();
        var tenantContext = A.Fake<ITenantContext>();
        var archiveStore = A.Fake<IArchiveRuntimeStore>();
        A.CallTo(() => systemContext.FindTenantContextAsync(tenant)).Returns(Task.FromResult(tenantContext));
        A.CallTo(() => tenantContext.GetArchiveRuntimeStore()).Returns(archiveStore);
        A.CallTo(() => archiveStore.GetAsync(archiveRtId)).Returns(Task.FromResult<ArchiveSnapshot?>(
            new ArchiveSnapshot(archiveRtId, AccountType, CkArchiveStatus.Activated, "A",
                [new CkArchiveColumnSpec("Name", false, false), new CkArchiveColumnSpec("PasswordHash", false, false)])));
        GivenSystemSessionIsExpected();

        var rtId = OctoObjectId.GenerateNewId();
        var persisted = new RtEntity(AccountType, rtId);
        persisted.SetAttributeRawValue("Name", "n");
        persisted.SetAttributeRawValue("PasswordHash", "secret-hash");
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync(Session, A<RtEntityId>._))
            .Returns(Task.FromResult<RtEntity?>(persisted));
        var update = EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(AccountType, rtId),
            new RtEntity(AccountType, rtId));
        var (dataContext, nodeContext, next) = PrepareTest(new BackfillFromRtEntityNodeConfiguration
            { Path = "$.u", ArchiveRtId = archiveRtId.ToString() });
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$.u")).Returns([update]);

        await new BackfillFromRtEntityNode(next, EtlContext, systemContext, _cache)
            .ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("n", update.RtEntity!.Attributes["Name"]);
        Assert.False(update.RtEntity.Attributes.ContainsKey("PasswordHash"));
    }

    [Fact]
    public void FromWatchRtEntity_TheChangedEntityAndItsPreviousState_CarryNoHiddenValue()
    {
        var document = UserWithSecrets();
        var before = Account(("Name", "old"), ("PasswordHash", "old-hash"));
        var update = A.Fake<IUpdateInfo<RtEntity>>();
        A.CallTo(() => update.Document).Returns(document);
        A.CallTo(() => update.DocumentBeforeChange).Returns(before);
        A.CallTo(() => update.UpdateType).Returns(UpdateTypes.Update);

        var projected = new FromWatchRtEntityNode(A.Fake<ISystemContext>(), _cache)
            .DropHidden(TenantRepository, Tenant, update);

        var json = JsonSerializer.Serialize(new { projected.Document, projected.DocumentBeforeChange });
        Assert.DoesNotContain("secret-hash", json);
        Assert.DoesNotContain("old-hash", json);
        Assert.Contains("old", json);
        Assert.Equal(UpdateTypes.Update, projected.UpdateType);
        // the change-stream documents themselves are not modified
        Assert.True(document.Attributes.ContainsKey("PasswordHash"));
    }

    [Fact]
    public void FromWatchRtEntity_AnUpdateWithoutHiddenValues_IsPassedThroughUnchanged()
    {
        var update = A.Fake<IUpdateInfo<RtEntity>>();
        A.CallTo(() => update.Document).Returns(Account(("Name", "n")));

        Assert.Same(update, new FromWatchRtEntityNode(A.Fake<ISystemContext>(), _cache)
            .DropHidden(TenantRepository, Tenant, update));
    }
}
