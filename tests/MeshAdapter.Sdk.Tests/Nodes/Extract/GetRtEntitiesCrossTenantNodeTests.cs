using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Microsoft.Extensions.DependencyInjection;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     The target-tenant setting of the three read nodes (AB#9768): with it, every read goes through
///     <see cref="Meshmakers.Octo.Sdk.MeshAdapter.IMeshEtlContext.OpenTenantAsync" /> (the guard) and uses only the
///     repository and the session the guard returned; the own repository and the node identity's own session are never
///     used. A refusal fails the node before anything is read. Without the setting, the guard is never asked.
/// </summary>
public class GetRtEntitiesCrossTenantNodeTests : SessionNodeTestBase
{
    private const string TargetTenantId = "group";
    private static readonly RtCkId<CkTypeId> ContactType = new("TestModel/Contact");

    private readonly ITenantRepository _targetRepository = A.Fake<ITenantRepository>();
    private readonly IOctoSession _targetSession = A.Fake<IOctoSession>();
    private readonly ICrossTenantAccess _access = A.Fake<ICrossTenantAccess>();

    private static readonly TenantTargetConfiguration Parent = new() { Relation = TenantRelation.Parent };

    public GetRtEntitiesCrossTenantNodeTests()
    {
        A.CallTo(() => _access.TenantId).Returns(TargetTenantId);
        A.CallTo(() => _access.Repository).Returns(_targetRepository);
        A.CallTo(() => _access.OpenSessionAsync()).Returns(Task.FromResult(_targetSession));
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                A<IDataContext>._, A<INodeContext>._))
            .Returns(Task.FromResult(_access));

        var contact = new RtEntity(ContactType, new OctoObjectId("000000000000000000000001")) { RtWellKnownName = "c1" };
        A.CallTo(() => _targetRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>([contact], 1, null, null)));
        A.CallTo(() => _targetRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>([contact], 1, null, null)));
    }

    private void GivenTheGuardRefuses()
    {
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                A<IDataContext>._, A<INodeContext>._))
            .ThrowsAsync(CrossTenantAccessDeniedException.Forbidden("transformations/0", "Parent",
                CrossTenantRefusalReason.IdentityNotProvable));
    }

    private void AssertOwnTenantNeverRead()
    {
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
        A.CallTo(() => EtlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
    }

    private void AssertTargetNeverRead()
    {
        A.CallTo(() => _targetRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
        A.CallTo(() => _targetRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
    }

    // ---- GetRtEntitiesByType@1 -----------------------------------------------------------------------------

    [Theory]
    [InlineData(NodeExecutionIdentity.Caller)]
    [InlineData(NodeExecutionIdentity.ServiceAccount)]
    public async Task ByType_WithTarget_ReadsOnlyTheTarget_WithTheSessionTheGuardProved(NodeExecutionIdentity identity)
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = ContactType, TargetPath = "$.contacts", TargetTenant = Parent, Identity = identity
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.OpenTenantAsync(Parent, identity, dataContext, nodeContext))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _targetRepository.GetRtEntitiesByTypeAsync(_targetSession, ContactType,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustHaveHappenedOnceExactly();
        AssertOwnTenantNeverRead();
        // Hidden attributes are those of the tenant the data comes from.
        A.CallTo(() => CkCache.IsTenantLoaded(TargetTenantId)).MustHaveHappened();
        A.CallTo(() => dataContext.Set("$.contacts", A<IResultSet<RtEntity>?>.That.Matches(r => r!.TotalCount == 1),
            A<DocumentModes>._, A<ValueKinds>._, A<TargetValueWriteModes>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ByType_InterfaceVariant_WithTarget_QueriesTheTargetRepository()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkInterfaceId = "System/Named-1", Take = 10, TargetPath = "$.named", TargetTenant = Parent
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => _targetRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
                A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._))
            .Returns(new RtInterfaceQueryResult([], 0));

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _targetRepository.GetRtEntitiesByInterfaceAsync(_targetSession, A<RtCkId<CkInterfaceId>>._,
            A<RtEntityQueryOptions>._, 0, 10, true)).MustHaveHappenedOnceExactly();
        A.CallTo(() => TenantRepository.GetRtEntitiesByInterfaceAsync(A<IOctoSession>._, A<RtCkId<CkInterfaceId>>._,
            A<RtEntityQueryOptions>._, A<int>._, A<int>._, A<bool>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task ByType_RefusedByTheGuard_FailsWithTheForbiddenError_AndReadsNothing()
    {
        GivenTheGuardRefuses();
        var config = new GetRtEntitiesByTypeNodeConfiguration
        {
            CkTypeId = ContactType, TargetPath = "$.contacts", TargetTenant = Parent
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        var nextCalled = false;

        var refused = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode((_, _) =>
            {
                nextCalled = true;
                return Task.CompletedTask;
            }, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantAccessCodes.Forbidden, refused.ErrorCode);
        Assert.False(nextCalled);
        AssertOwnTenantNeverRead();
        AssertTargetNeverRead();
    }

    [Fact]
    public async Task ByType_WithoutTarget_NeverAsksTheGuard_AndReadsTheOwnTenantAsBefore()
    {
        var config = new GetRtEntitiesByTypeNodeConfiguration { CkTypeId = ContactType, TargetPath = "$.contacts" };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>([], 0, null, null)));

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
            A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        A.CallTo(() => EtlContext.GetSessionForAsync(NodeExecutionIdentity.Caller)).MustHaveHappenedOnceExactly();
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(Session, ContactType, A<RtEntityQueryOptions>._,
            A<int?>._, A<int?>._)).MustHaveHappenedOnceExactly();
        AssertTargetNeverRead();
    }

    // ---- GetRtEntitiesById@1 -------------------------------------------------------------------------------

    [Fact]
    public async Task ById_WithTarget_ReadsOnlyTheTarget()
    {
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = ContactType,
            RtIds = [new OctoObjectId("000000000000000000000001")],
            TargetPath = "$.contact",
            TargetTenant = new TenantTargetConfiguration { Relation = TenantRelation.Sibling, TenantId = "b" },
            Identity = NodeExecutionIdentity.ServiceAccount
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await new GetRtEntitiesByIdNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.OpenTenantAsync(config.TargetTenant, NodeExecutionIdentity.ServiceAccount,
            dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        A.CallTo(() => _targetRepository.GetRtEntitiesByIdAsync(_targetSession, ContactType,
                A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .MustHaveHappenedOnceExactly();
        AssertOwnTenantNeverRead();
    }

    [Fact]
    public async Task ById_RefusedByTheGuard_FailsAndReadsNothing()
    {
        GivenTheGuardRefuses();
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = ContactType,
            RtIds = [new OctoObjectId("000000000000000000000001")],
            TargetPath = "$.contact",
            TargetTenant = Parent
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByIdNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        AssertOwnTenantNeverRead();
        AssertTargetNeverRead();
    }

    // ---- GetRtEntitiesByWellKnownName@1 --------------------------------------------------------------------

    private static GetRtEntitiesByWellKnownNameNodeConfiguration WellKnownNameConfig() => new()
    {
        Path = "$.items",
        CkTypeId = ContactType,
        WellKnownNamePath = "$.name",
        RtIdTargetPath = "$.rtId",
        CkTypeIdTargetPath = "$.ckTypeId",
        ModOperationPath = "$.modOperation",
        TargetTenant = Parent
    };

    [Fact]
    public async Task ByWellKnownName_WithTarget_ResolvesAgainstTheTarget()
    {
        using var doc = JsonDocument.Parse("""{"items":[{"name":"c1"}]}""");
        using var dataContext = new DataContextImpl(doc.RootElement);
        var (nodeContext, next) = PrepareNode(dataContext, WellKnownNameConfig(), "GetRtEntitiesByWellKnownName");

        await new GetRtEntitiesByWellKnownNameTypeNode(next, EtlContext, CkCache)
            .ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => _targetRepository.GetRtEntitiesByTypeAsync(_targetSession, ContactType,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustHaveHappenedOnceExactly();
        AssertOwnTenantNeverRead();
        Assert.Equal("000000000000000000000001", dataContext.Get<string>("$.items[0].rtId"));
    }

    [Fact]
    public async Task ByWellKnownName_RefusedByTheGuard_FailsAndReadsNothing()
    {
        GivenTheGuardRefuses();
        using var doc = JsonDocument.Parse("""{"items":[{"name":"c1"}]}""");
        using var dataContext = new DataContextImpl(doc.RootElement);
        var (nodeContext, next) = PrepareNode(dataContext, WellKnownNameConfig(), "GetRtEntitiesByWellKnownName");

        await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByWellKnownNameTypeNode(next, EtlContext, CkCache)
                .ProcessObjectAsync(dataContext, nodeContext));

        AssertOwnTenantNeverRead();
        AssertTargetNeverRead();
    }

    // ---- Session lifetime: the session the guard opened in the target is disposed on every path ----------

    private async Task RunReadAsync(string node)
    {
        switch (node)
        {
            case "ByType":
            {
                var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByTypeNodeConfiguration
                {
                    CkTypeId = ContactType, TargetPath = "$.contacts", TargetTenant = Parent
                });
                await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext,
                    nodeContext);
                break;
            }
            case "ById":
            {
                var (dataContext, nodeContext, next) = PrepareTest(new GetRtEntitiesByIdNodeConfiguration
                {
                    CkTypeId = ContactType,
                    RtIds = [new OctoObjectId("000000000000000000000001")],
                    TargetPath = "$.contact",
                    TargetTenant = Parent
                });
                await new GetRtEntitiesByIdNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext,
                    nodeContext);
                break;
            }
            default:
            {
                using var doc = JsonDocument.Parse("""{"items":[{"name":"c1"}]}""");
                using var dataContext = new DataContextImpl(doc.RootElement);
                var (nodeContext, next) =
                    PrepareNode(dataContext, WellKnownNameConfig(), "GetRtEntitiesByWellKnownName");
                await new GetRtEntitiesByWellKnownNameTypeNode(next, EtlContext, CkCache)
                    .ProcessObjectAsync(dataContext, nodeContext);
                break;
            }
        }
    }

    [Theory]
    [InlineData("ByType")]
    [InlineData("ById")]
    [InlineData("ByWellKnownName")]
    public async Task CrossTenantSession_IsDisposed_AfterARead(string node)
    {
        await RunReadAsync(node);

        A.CallTo(() => _targetSession.Dispose()).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("ByType")]
    [InlineData("ById")]
    [InlineData("ByWellKnownName")]
    public async Task CrossTenantSession_IsDisposed_WhenTheQueryThrows(string node)
    {
        A.CallTo(() => _targetRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ThrowsAsync(new InvalidOperationException("query failed"));
        A.CallTo(() => _targetRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .ThrowsAsync(new InvalidOperationException("query failed"));

        await Assert.ThrowsAsync<InvalidOperationException>(() => RunReadAsync(node));

        A.CallTo(() => _targetSession.Dispose()).MustHaveHappenedOnceExactly();
    }

    private static (INodeContext nodeContext, NodeDelegate next) PrepareNode<TConfig>(
        IDataContext dataContext, TConfig config, string nodeName) where TConfig : INodeConfiguration
    {
        var rootNodeContext = NodeContext.CreateRootNodeContext(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode(nodeName, 0, config!, dataContext);
        return (nodeContext, A.Fake<NodeDelegate>());
    }
}
