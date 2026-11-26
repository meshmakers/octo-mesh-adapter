using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Common;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;
using Meshmakers.Octo.Services.Infrastructure.TenantGeneration;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
///     The multi-tenant read of GetRtEntitiesByType@1 and GetRtEntitiesById@1 (AB#9771, <c>targetTenants</c>): every
///     target is opened through the guard as a Child target before anything is read; <c>onRefused: Fail</c> reads
///     nothing when one is refused, <c>Report</c> lists it and reads the others; each item names its source tenant.
/// </summary>
public class GetRtEntitiesMultiTenantNodeTests : SessionNodeTestBase
{
    private static readonly RtCkId<CkTypeId> InvoiceType = new("TestModel/Invoice");

    private readonly Dictionary<string, ITenantRepository> _repositories = new(StringComparer.Ordinal);
    private readonly HashSet<string> _refused = [];
    private string _refusalReason = CrossTenantRefusalReason.IdentityNotProvable;

    public GetRtEntitiesMultiTenantNodeTests()
    {
        foreach (var tenantId in new[] { "a", "b", "c" })
        {
            var repository = A.Fake<ITenantRepository>();
            var invoice = new RtEntity(InvoiceType, OctoObjectId.GenerateNewId());
            invoice.SetAttributeRawValue("InvoiceNumber", $"INV-{tenantId}");
            A.CallTo(() => repository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                    A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
                .Returns(Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>([invoice], 1, null, null)));
            A.CallTo(() => repository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                    A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
                .Returns(Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>([invoice], 1, null, null)));
            _repositories[tenantId] = repository;
        }

        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._))
            .Returns(Task.FromResult<IReadOnlyList<string>>(["a", "b", "c"]));
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                A<IDataContext>._, A<INodeContext>._))
            .ReturnsLazily((TenantTargetConfiguration target, NodeExecutionIdentity _, IDataContext _,
                INodeContext _) =>
            {
                if (target.Relation != TenantRelation.Child || _refused.Contains(target.TenantId!))
                {
                    throw CrossTenantAccessDeniedException.Forbidden("transformations/0", "Child", _refusalReason);
                }

                var access = A.Fake<ICrossTenantAccess>();
                A.CallTo(() => access.TenantId).Returns(target.TenantId!);
                A.CallTo(() => access.Repository).Returns(_repositories[target.TenantId!]);
                A.CallTo(() => access.OpenSessionAsync()).Returns(Task.FromResult(A.Fake<IOctoSession>()));
                return Task.FromResult(access);
            });
    }

    private static GetRtEntitiesByTypeNodeConfiguration ByType(TenantReadSetConfiguration set,
        NodeExecutionIdentity identity = NodeExecutionIdentity.Caller) => new()
    {
        CkTypeId = InvoiceType, TargetPath = "$.invoices", TargetTenants = set, Identity = identity
    };

    private void AssertNothingRead()
    {
        foreach (var repository in _repositories.Values.Append(TenantRepository))
        {
            A.CallTo(() => repository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
            A.CallTo(() => repository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
                .MustNotHaveHappened();
        }

        A.CallTo(() => EtlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task AllChildren_EachOpenedByTheGuardAsChild_EachItemNamesItsSource()
    {
        var (dataContext, nodeContext, next) =
            PrepareTest(ByType(new TenantReadSetConfiguration { Relation = TenantRelation.Child }));
        JsonObject? result = null;
        A.CallTo(() => dataContext.Set("$.invoices", A<JsonObject?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonObject? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                result = value);

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        foreach (var tenantId in new[] { "a", "b", "c" })
        {
            A.CallTo(() => EtlContext.OpenTenantAsync(
                A<TenantTargetConfiguration>.That.Matches(t =>
                    t.Relation == TenantRelation.Child && t.TenantId == tenantId && t.TenantIdPath == null),
                NodeExecutionIdentity.Caller, dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        }

        Assert.NotNull(result);
        var items = result!["Items"]!.AsArray();
        Assert.Equal(3, items.Count);
        foreach (var item in items)
        {
            var source = item!["SourceTenantId"]!.GetValue<string>();
            Assert.Equal($"INV-{source}", item["Attributes"]!["InvoiceNumber"]!.GetValue<string>());
        }

        Assert.Equal(3, result["TotalCount"]!.GetValue<long>());
        Assert.Equal(3, result["Tenants"]!.AsArray().Count);
        Assert.Empty(result["RefusedTenants"]!.AsArray());
        // The own tenant is never read.
        A.CallTo(() => TenantRepository.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
        A.CallTo(() => EtlContext.GetSessionForAsync(A<NodeExecutionIdentity>._)).MustNotHaveHappened();
    }

    [Theory]
    [InlineData(null)]
    [InlineData(CrossTenantRefusalHandling.Fail)]
    public async Task OneRefused_Fail_ThrowsNamingIt_AndReadsNoTenantAtAll(CrossTenantRefusalHandling? onRefused)
    {
        _refused.Add("c");
        var (dataContext, nodeContext, next) = PrepareTest(ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, OnRefused = onRefused
        }));

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(new[] { "c" }, ex.RefusedTenantIds);
        Assert.Contains("'c'", ex.Message);
        Assert.Contains(CrossTenantAccessCodes.Forbidden, ex.Message);
        AssertNothingRead();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task OneRefused_Report_ListsIt_AndReadsOnlyTheOthers()
    {
        _refused.Add("b");
        var (dataContext, nodeContext, next) = PrepareTest(ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, OnRefused = CrossTenantRefusalHandling.Report
        }));
        JsonObject? result = null;
        A.CallTo(() => dataContext.Set("$.invoices", A<JsonObject?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonObject? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                result = value);

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        var refused = Assert.Single(result!["RefusedTenants"]!.AsArray());
        Assert.Equal("b", refused!["TenantId"]!.GetValue<string>());
        Assert.Equal(CrossTenantAccessCodes.Forbidden, refused["ErrorCode"]!.GetValue<string>());
        Assert.Equal(new[] { "a", "c" },
            result["Items"]!.AsArray().Select(i => i!["SourceTenantId"]!.GetValue<string>()).ToArray());
        A.CallTo(() => _repositories["b"].GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
        Assert.DoesNotContain("INV-b", result.ToJsonString());
    }

    [Theory]
    [InlineData(CrossTenantRefusalReason.SystemIdentityRefused)]
    [InlineData(CrossTenantRefusalReason.InvalidTargetConfiguration)]
    // A registry, identity-service or repository outage is no refusal of the tenant: an empty consolidation must not
    // look like a valid one.
    [InlineData(CrossTenantRefusalReason.TargetUnverifiable)]
    public async Task RefusalNotAboutTheTarget_FailsEvenWithReport(string reason)
    {
        _refused.Add("a");
        _refusalReason = reason;
        var (dataContext, nodeContext, next) = PrepareTest(ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, OnRefused = CrossTenantRefusalHandling.Report
        }));

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(reason, ex.ReasonCode);
        AssertNothingRead();
    }

    public enum Outage
    {
        Registry,
        IdentityService,
        ServiceAccount
    }

    /// <summary>The real guard behind the node's <see cref="IMeshEtlContext.OpenTenantAsync" />, for the tree g → a, b.</summary>
    private sealed class RealGuard
    {
        public ITenantRegistryReader Registry { get; } = A.Fake<ITenantRegistryReader>();
        public ICrossTenantIdentityProvider Provider { get; } = A.Fake<ICrossTenantIdentityProvider>();
        public IPipelineIdentityResolver Resolver { get; } = A.Fake<IPipelineIdentityResolver>();
        public ISystemContext SystemContext { get; } = A.Fake<ISystemContext>();
        public ITenantGenerationReader Generations { get; } = A.Fake<ITenantGenerationReader>();

        public RealGuard()
        {
            A.CallTo(() => Registry.GetAsync(A<bool>._, A<CancellationToken>._))
                .Returns(new TenantRegistrySnapshot("octosystem", [("g", null), ("a", "g"), ("b", "g")]));
            A.CallTo(() => Resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
                .Returns(new ValueTask<RtSecurityContext>(RtSecurityContext.ForUser("sa-of-g", ["AdminOfG"])));
            A.CallTo(() => Generations.GetGenerationAsync(A<string>._, A<bool>._, A<CancellationToken>._))
                .Returns(new ValueTask<string?>("gen-1"));
        }

        public void WireInto(IMeshEtlContext etlContext)
        {
            var guard = new CrossTenantAccessGuard(SystemContext, A.Fake<ICkCacheService>(), Registry, Provider,
                NullLogger<CrossTenantAccessGuard>.Instance, Generations);
            var caller = new VerifiedPrincipal("user-1", "g", null, null, ["AdminOfG"]);
            var proofs = new CrossTenantProofCache();
            A.CallTo(() => etlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
                    A<IDataContext>._, A<INodeContext>._))
                .ReturnsLazily((TenantTargetConfiguration target, NodeExecutionIdentity id, IDataContext data,
                    INodeContext node) => guard.OpenAsync(new CrossTenantAccessRequest("g", target, id, data,
                    node.NodePath.ToString(), caller, "caller-token", Resolver, A.Fake<IGlobalConfiguration>(),
                    proofs)));
        }
    }

    private static GetRtEntitiesByTypeNodeConfiguration ReportOnAAndB(NodeExecutionIdentity identity) =>
        ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, TenantIds = ["a", "b"], OnRefused = CrossTenantRefusalHandling.Report
        }, identity);

    [Theory]
    [InlineData(Outage.Registry, NodeExecutionIdentity.Caller)]
    [InlineData(Outage.Registry, NodeExecutionIdentity.ServiceAccount)]
    [InlineData(Outage.IdentityService, NodeExecutionIdentity.Caller)]
    [InlineData(Outage.IdentityService, NodeExecutionIdentity.ServiceAccount)]
    [InlineData(Outage.ServiceAccount, NodeExecutionIdentity.ServiceAccount)]
    public async Task AnOutage_WithExplicitTenants_FailsTheExecution_EvenWithReport(Outage outage,
        NodeExecutionIdentity identity)
    {
        // An outage on the pipeline's own side (registry, identity service unreachable or 5xx, no token of the
        // pipeline's own account) is no refusal of a company, so a consolidation with onRefused: Report must not
        // succeed with every company dropped.
        var real = new RealGuard();
        switch (outage)
        {
            case Outage.Registry:
                A.CallTo(() => real.Registry.GetAsync(A<bool>._, A<CancellationToken>._))
                    .ThrowsAsync(new TimeoutException("registry down"));
                break;
            case Outage.IdentityService:
                A.CallTo(() => real.Provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
                    .ThrowsAsync(new CrossTenantIdentityUnavailableException("identity down"));
                break;
            case Outage.ServiceAccount:
                A.CallTo(() => real.Resolver.ResolveServiceAccountAsync(A<CancellationToken>._))
                    .Throws(new InvalidOperationException("token endpoint down"));
                break;
        }

        real.WireInto(EtlContext);
        var (dataContext, nodeContext, next) = PrepareTest(ReportOnAAndB(identity));

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantRefusalReason.TargetUnverifiable, ex.ReasonCode);
        Assert.DoesNotContain("down", ex.Message);
        A.CallTo(() => real.SystemContext.FindTenantRepositoryAsync(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        AssertNothingRead();
    }

    [Theory]
    [InlineData(NodeExecutionIdentity.Caller)]
    [InlineData(NodeExecutionIdentity.ServiceAccount)]
    public async Task IdentityRefusingTheExchangeForOneCompany_IsARefusal_ReportListsIt_AndReadsTheOther(
        NodeExecutionIdentity identity)
    {
        // The counterpart of the outage: the identity service answers and refuses the exchange for a (no grant, not
        // provisioned; the provider answers null). That is a refusal of a only: Report lists it and reads b.
        var real = new RealGuard();
        A.CallTo(() => real.Provider.ProveAsync(A<CrossTenantProofRequest>._, A<CancellationToken>._))
            .ReturnsLazily((CrossTenantProofRequest r, CancellationToken _) => Task.FromResult(
                r.TargetTenantId == "a"
                    ? null
                    : new CrossTenantProof(r.TargetTenantId, "gen-1",
                        RtSecurityContext.ForUser("xt_g_proven", ["TenantDataReader"]))));
        var repositoryOfB = A.Fake<ITenantRepository>(o => o.Implements<ISecureSessionFactory>());
        A.CallTo(() => ((ISecureSessionFactory)repositoryOfB).GetSessionAsync(A<RtSecurityContext>._))
            .Returns(Task.FromResult(A.Fake<IOctoSession>()));
        var invoice = new RtEntity(InvoiceType, OctoObjectId.GenerateNewId());
        invoice.SetAttributeRawValue("InvoiceNumber", "INV-b");
        A.CallTo(() => repositoryOfB.GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(new ResultSet<RtEntity>([invoice], 1, null, null)));
        A.CallTo(() => real.SystemContext.FindTenantRepositoryAsync("b")).Returns(repositoryOfB);
        real.WireInto(EtlContext);
        var (dataContext, nodeContext, next) = PrepareTest(ReportOnAAndB(identity));
        JsonObject? result = null;
        A.CallTo(() => dataContext.Set("$.invoices", A<JsonObject?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonObject? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                result = value);

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(result);
        var refused = Assert.Single(result["RefusedTenants"]!.AsArray())!;
        Assert.Equal("a", refused["TenantId"]!.GetValue<string>());
        Assert.Equal(CrossTenantAccessCodes.Forbidden, refused["ErrorCode"]!.GetValue<string>());
        var item = Assert.Single(result["Items"]!.AsArray())!;
        Assert.Equal("b", item["SourceTenantId"]!.GetValue<string>());
        A.CallTo(() => real.SystemContext.FindTenantRepositoryAsync("a")).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData(CrossTenantRefusalHandling.Fail)]
    [InlineData(CrossTenantRefusalHandling.Report)]
    public async Task SystemIdentity_WithoutAnyChild_IsRefused_BeforeTheChildListIsRead(
        CrossTenantRefusalHandling onRefused)
    {
        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._))
            .Returns(Task.FromResult<IReadOnlyList<string>>([]));
        var (dataContext, nodeContext, next) = PrepareTest(ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, OnRefused = onRefused
        }, NodeExecutionIdentity.System));

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantRefusalReason.SystemIdentityRefused, ex.ReasonCode);
        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._)).MustNotHaveHappened();
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
            A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        A.CallTo(() => next(A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        AssertNothingRead();
    }

    [Fact]
    public async Task TenantIds_OpensExactlyThose_DeduplicatedAndTrimmed_WithoutAskingTheRegistry()
    {
        var (dataContext, nodeContext, next) = PrepareTest(ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, TenantIds = ["a", " a ", "b"]
        }));

        await new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._)).MustNotHaveHappened();
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
            A<IDataContext>._, A<INodeContext>._)).MustHaveHappenedTwiceExactly();
        A.CallTo(() => _repositories["c"].GetRtEntitiesByTypeAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
    }

    public static TheoryData<TenantReadSetConfiguration, bool> InvalidSets => new()
    {
        { new TenantReadSetConfiguration { Relation = null }, false },
        { new TenantReadSetConfiguration { Relation = TenantRelation.Parent }, false },
        { new TenantReadSetConfiguration { Relation = TenantRelation.Sibling, TenantIds = ["b"] }, false },
        { new TenantReadSetConfiguration { Relation = TenantRelation.Child, TenantIds = [] }, false },
        { new TenantReadSetConfiguration { Relation = TenantRelation.Child, TenantIds = [" "] }, false },
        // targetTenant and targetTenants together
        { new TenantReadSetConfiguration { Relation = TenantRelation.Child }, true }
    };

    [Theory]
    [MemberData(nameof(InvalidSets))]
    public async Task InvalidSet_IsRefusedAsConfigurationError_BeforeTheGuardIsAsked(TenantReadSetConfiguration set,
        bool withSingleTarget)
    {
        var config = ByType(set) with
        {
            TargetTenant = withSingleTarget ? new TenantTargetConfiguration { Relation = TenantRelation.Parent } : null
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(CrossTenantRefusalReason.InvalidTargetConfiguration, ex.ReasonCode);
        A.CallTo(() => EtlContext.OpenTenantAsync(A<TenantTargetConfiguration>._, A<NodeExecutionIdentity>._,
            A<IDataContext>._, A<INodeContext>._)).MustNotHaveHappened();
        A.CallTo(() => EtlContext.ListChildTenantsAsync(A<INodeContext>._)).MustNotHaveHappened();
        AssertNothingRead();
    }

    [Fact]
    public async Task ById_ReadsTheIdsFromEveryChild_AndNamesTheSource()
    {
        _refused.Add("c");
        var config = new GetRtEntitiesByIdNodeConfiguration
        {
            CkTypeId = InvoiceType,
            RtIds = [new OctoObjectId("000000000000000000000001")],
            TargetPath = "$.invoices",
            TargetTenants = new TenantReadSetConfiguration
            {
                Relation = TenantRelation.Child, OnRefused = CrossTenantRefusalHandling.Report
            }
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        JsonObject? result = null;
        A.CallTo(() => dataContext.Set("$.invoices", A<JsonObject?>._, A<DocumentModes>._, A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, JsonObject? value, DocumentModes _, ValueKinds _, TargetValueWriteModes _) =>
                result = value);

        await new GetRtEntitiesByIdNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(new[] { "a", "b" },
            result!["Items"]!.AsArray().Select(i => i!["SourceTenantId"]!.GetValue<string>()).ToArray());
        Assert.Equal("c", result["RefusedTenants"]![0]!["TenantId"]!.GetValue<string>());
        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, A<RtCkId<CkTypeId>>._,
            A<IReadOnlyList<OctoObjectId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._)).MustNotHaveHappened();
    }
}
