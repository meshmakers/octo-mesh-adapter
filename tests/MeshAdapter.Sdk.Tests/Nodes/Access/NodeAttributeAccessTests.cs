using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;
using static MeshAdapter.Sdk.Tests.Helpers.AttributeAccessTestModel;

namespace MeshAdapter.Sdk.Tests.Nodes.Access;

/// <summary>
///     AB#7768 — the extract and load nodes honour the attribute access (Hidden never read or filtered on;
///     MethodOnly written only in a method run; ReadOnly never written), also in dry runs. The access rule itself is
///     covered by <see cref="RtAttributeAccessGuardTests" />.
/// </summary>
public class NodeAttributeAccessTests : SessionNodeTestBase
{
    private readonly Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService _cache = Cache();

    public NodeAttributeAccessTests()
    {
        ConfigureRun(EtlContext, null);
        GivenSystemSessionIsExpected();
    }

    private static IResultSet<RtEntity> ResultSet(params RtEntity[] entities)
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.Items).Returns(entities.ToList());
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Length);
        return resultSet;
    }

    private static RtEntity UserWithSecrets() => Account(("Name", "n"), ("NormalizedEmail", "A@B.C"),
        ("Created", "c"), ("Email", "e"), ("PasswordHash", "secret-hash"));

    private static List<RtEntity> Emitted(IDataContext dataContext, string path)
    {
        var emitted = new List<RtEntity>();
        foreach (var call in Fake.GetCalls(dataContext).Where(c => c.Method.Name == "Set"))
        {
            if (call.Arguments[0] as string == path && call.Arguments[1] is IResultSet<RtEntity> rs)
            {
                emitted.AddRange(rs.Items);
            }
        }

        return emitted;
    }

    private static void AssertEmittedWithoutHidden(IEnumerable<RtEntity> entities)
    {
        var entity = Assert.Single(entities);
        Assert.False(entity.Attributes.ContainsKey("PasswordHash"));
        Assert.Equal("n", entity.Attributes["Name"]);
        Assert.Equal("A@B.C", entity.Attributes["NormalizedEmail"]);
        Assert.Equal("c", entity.Attributes["Created"]); // ReadOnly is readable
        Assert.Equal("e", entity.Attributes["Email"]); // MethodOnly is readable
    }

    // ------------------------------------------------------- GetRtEntitiesByType

    [Fact]
    public async Task GetRtEntitiesByType_DropsHidden_KeepsMethodOnlyAndReadOnly()
    {
        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = AccountType, TargetPath = "$.result"
        });
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).Returns(ResultSet(UserWithSecrets()));

        await new GetRtEntitiesByTypeNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext);

        AssertEmittedWithoutHidden(Emitted(dataContext, "$.result"));
    }

    [Fact]
    public async Task GetRtEntitiesByType_FilterOnNormalizedEmail_StillWorks_WithoutTheHash()
    {
        // The meshmakers-app user lookup (document-analysis.yaml) filters System.Identity/User by NormalizedEmail.
        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = AccountType,
            TargetPath = "$.result",
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "NormalizedEmail", Operator = FieldFilterOperatorDto.Equals,
                    ComparisonValue = "A@B.C"
                }
            ]
        });
        RtEntityQueryOptions? options = null;
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Invokes(call => options = call.GetArgument<RtEntityQueryOptions>(2))
            .Returns(ResultSet(UserWithSecrets()));

        await new GetRtEntitiesByTypeNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal("NormalizedEmail", Assert.Single(options!.FieldFilters!).AttributePath);
        AssertEmittedWithoutHidden(Emitted(dataContext, "$.result"));
    }

    [Fact]
    public async Task GetRtEntitiesByType_FilterOnHidden_FailsTheNode_WithoutQueryingAndWithoutTheProbedValue()
    {
        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = AccountType,
            TargetPath = "$.result",
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "PasswordHash", Operator = FieldFilterOperatorDto.Equals,
                    ComparisonValue = "probed-value-123"
                }
            ]
        });

        var ex = await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal("PasswordHash", ex.AttributePath);
        Assert.Contains("PasswordHash", ex.Message);
        Assert.DoesNotContain("probed-value-123", ex.Message);
        Assert.False(string.IsNullOrEmpty(ex.Node));
        AssertNoSessionOpened();
        A.CallTo(TenantRepository).Where(c => c.Method.Name.StartsWith("GetRtEntitiesBy")).MustNotHaveHappened();
    }

    [Fact]
    public async Task GetRtEntitiesByType_SortOnHidden_FailsTheNode()
    {
        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = AccountType,
            TargetPath = "$.result",
            SortOrders = [new SortOrderDto { AttributeName = "PasswordHash", SortOrder = SortOrdersDto.Ascending }]
        });

        var ex = await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal("sort order", ex.Usage);
    }

    [Fact]
    public async Task GetRtEntitiesByType_ByInterface_FilterOnAHiddenAttributeOfAnImplementer_Fails_AndRowsAreProjected()
    {
        var iface = new RtCkId<CkInterfaceId>("Test/Named-1");
        var accountId = new CkId<CkTypeId>("Test-1.0.0/Account");
        var graph = new Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph.CkInterfaceGraph(
            new CkId<CkInterfaceId>("Test-1.0.0/Named-1"), null,
            new Dictionary<CkId<CkAttributeId>, Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph.CkTypeAttributeGraph>(),
            [accountId]);
        A.CallTo(() => _cache.GetRtCkInterface(Tenant, A<RtCkId<CkInterfaceId>>._)).Returns(graph);

        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByTypeNodeConfiguration
        {
            CkInterfaceId = "Test/Named-1",
            Take = 10,
            TargetPath = "$.result",
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "PasswordHash", Operator = FieldFilterOperatorDto.Equals, ComparisonValue = "x"
                }
            ]
        });
        Assert.NotNull(iface);

        await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext));
    }

    // --------------------------------------------------- GetRtEntitiesById / WKN

    [Fact]
    public async Task GetRtEntitiesById_DropsHidden()
    {
        var entity = UserWithSecrets();
        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = AccountType, RtIds = [entity.RtId], TargetPath = "$.result"
        });
        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(ResultSet(entity));

        await new GetRtEntitiesByIdNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext);

        AssertEmittedWithoutHidden(Emitted(dataContext, "$.result"));
    }

    [Fact]
    public async Task GetRtEntitiesById_FilterOnHidden_Fails()
    {
        var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = AccountType,
            RtIds = [OctoObjectId.GenerateNewId()],
            TargetPath = "$.result",
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "Profile.Token", Operator = FieldFilterOperatorDto.Equals, ComparisonValue = "x"
                }
            ]
        });

        await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetRtEntitiesByIdNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext));
    }

    [Fact]
    public async Task GetRtEntitiesByWellKnownName_DoesNotCopyHiddenAttributesIntoTheItems()
    {
        var entity = UserWithSecrets();
        using var doc = JsonDocument.Parse("""{ "items": [ { "name": "wkn" } ] }""");
        using var dataContext = new DataContextImpl(doc.RootElement);
        var config = new GetRtEntitiesByWellKnownNameNodeConfiguration
        {
            CkTypeId = AccountType,
            Path = "$.items",
            WellKnownNamePath = "$.name",
            AttributeTargetPath = "$.attrs",
            RtIdTargetPath = "$.rtId",
            CkTypeIdTargetPath = "$.ckTypeId",
            ModOperationPath = "$.modOp"
        };
        var rootNodeContext = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("GetRtEntitiesByWellKnownName", 0, config, dataContext);
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).Returns(ResultSet(entity));

        await new GetRtEntitiesByWellKnownNameTypeNode(A.Fake<NodeDelegate>(), EtlContext, _cache)
            .ProcessObjectAsync(dataContext, nodeContext);

        var json = dataContext.Get<System.Text.Json.Nodes.JsonNode>("$")!.ToJsonString();
        Assert.Contains("A@B.C", json);
        Assert.DoesNotContain("secret-hash", json);
        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);
    }

    // ------------------------------------------------ GetOrCreate / CheckDuplicate

    [Fact]
    public async Task GetOrCreateRtEntitiesByType_FilterOnHidden_Fails()
    {
        var (dataContext, nodeContext, next) = PrepareTest(new GetOrCreateRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = AccountType,
            FieldFilters =
            [
                new FieldFilterWithPathDto
                {
                    AttributePath = "PasswordHash", Operator = FieldFilterOperatorDto.Equals, ComparisonValue = "x"
                }
            ]
        });

        await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetOrCreateRtEntitiesByTypeNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext));
    }

    private async Task RunCheckDuplicate(string attribute, IResultSet<RtEntity> result, string? existingPath = null)
    {
        var config = new CheckDuplicateNodeConfiguration
        {
            CkTypeId = AccountType,
            AttributeName = attribute,
            ValuePath = "$.value",
            TargetPath = "$.isDuplicate",
            ExistingEntityPath = existingPath
        };
        var dataContext = new DataContextImpl(JsonDocument.Parse("""{ "value": "A@B.C" }"""));
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = root.RegisterChildNode("CheckDuplicate", 0, config, dataContext);
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).Returns(result);

        await new CheckDuplicateNode(A.Fake<NodeDelegate>(), EtlContext, _cache)
            .ProcessObjectAsync(dataContext, nodeContext);
        LastDataContext = dataContext;
    }

    private IDataContext? LastDataContext { get; set; }

    [Fact]
    public async Task CheckDuplicate_OnAHiddenAttribute_Fails_NoOracle()
    {
        await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            RunCheckDuplicate("PasswordHash", ResultSet(UserWithSecrets())));
        A.CallTo(TenantRepository).Where(c => c.Method.Name.StartsWith("GetRtEntitiesBy")).MustNotHaveHappened();
    }

    [Fact]
    public async Task CheckDuplicate_ExistingEntity_IsEmittedWithoutHidden()
    {
        await RunCheckDuplicate("NormalizedEmail", ResultSet(UserWithSecrets()), "$.existing");

        var json = LastDataContext!.Get<System.Text.Json.Nodes.JsonNode>("$")!.ToJsonString();
        Assert.Contains("A@B.C", json);
        Assert.DoesNotContain("secret-hash", json);
    }

    // ---------------------------------------------------------------- ApplyChanges

    private static readonly IPipelineExecutionMode DryRun = new DefaultPipelineExecutionMode { IsDryRun = true };

    private async Task ApplyV1(List<EntityUpdateInfo<RtEntity>> data, IPipelineExecutionMode? mode = null)
    {
        var (dataContext, nodeContext, next) = PrepareTest(new ApplyChangesNodeConfiguration { Path = "$.u" },
            executionMode: mode);
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$.u")).Returns(data);
        await new ApplyChangesNode(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext);
    }

    private async Task ApplyV2(List<EntityUpdateInfo<RtEntity>> data, IPipelineExecutionMode? mode = null)
    {
        var (dataContext, nodeContext, next) = PrepareTest(
            new ApplyChangesNodeConfiguration2 { EntityUpdatesPath = "$.u" }, executionMode: mode);
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>("$.u")).Returns(data);
        await new ApplyChangesNode2(next, EtlContext, _cache).ProcessObjectAsync(dataContext, nodeContext);
    }

    private void AssertNothingWritten()
    {
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync").MustNotHaveHappened();
    }

    private void AssertWritten() =>
        A.CallTo(TenantRepository).Where(c => c.Method.Name == "ApplyChangesAsync").MustHaveHappened();

    public static TheoryData<bool> Versions => new() { true, false };

    private Task Apply(bool v1, List<EntityUpdateInfo<RtEntity>> data, IPipelineExecutionMode? mode = null) =>
        v1 ? ApplyV1(data, mode) : ApplyV2(data, mode);

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_HiddenAttribute_IsRefused_AndNothingIsWritten(bool v1)
    {
        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Apply(v1, [Update(("PasswordHash", "planted"))]));
        AssertNothingWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_MethodOnlyAttribute_IsRefusedInAnOrdinaryPipeline(bool v1)
    {
        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Apply(v1, [Update(("Email", "x@y"))]));
        AssertNothingWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_MethodOnlyAttribute_IsWrittenInTheMethodHandlerRun(bool v1)
    {
        ConfigureRun(EtlContext, MethodRunMarker());

        await Apply(v1, [Update(("Email", "x@y"), ("Name", "n"))]);

        AssertWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_MethodOnlyAttribute_IsRefusedForAMarkerOfAnotherPipeline(bool v1)
    {
        ConfigureRun(EtlContext, MethodRunMarker() with { PipelineRtId = OctoObjectId.GenerateNewId().ToString() });

        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Apply(v1, [Update(("Email", "x@y"))]));
        AssertNothingWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_ReadOnlyAttribute_IsRefused_EvenInAMethodRun(bool v1)
    {
        ConfigureRun(EtlContext, MethodRunMarker());

        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Apply(v1, [Update(("Created", "c"))]));
        AssertNothingWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_ReadWriteAttributes_AreWritten(bool v1)
    {
        await Apply(v1, [Update(("Name", "n"), ("NormalizedEmail", "a@b"))]);

        AssertWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_DryRun_ReportsTheRefusal_InsteadOfWouldApply(bool v1)
    {
        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Apply(v1, [Update(("PasswordHash", "planted"))], DryRun));
        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Apply(v1, [Update(("Email", "x"))], DryRun));
        AssertNothingWritten();
    }

    [Theory]
    [MemberData(nameof(Versions))]
    public async Task ApplyChanges_DryRun_OfAnAllowedBatch_WritesNothing(bool v1)
    {
        ConfigureRun(EtlContext, MethodRunMarker());

        await Apply(v1, [Update(("Email", "x@y"))], DryRun);

        AssertNothingWritten();
    }
}
