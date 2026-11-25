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
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CrossTenant;

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
    public async Task RefusalNotAboutTheTarget_FailsEvenWithReport(string reason)
    {
        _refused.Add("a");
        _refusalReason = reason;
        var (dataContext, nodeContext, next) = PrepareTest(ByType(new TenantReadSetConfiguration
        {
            Relation = TenantRelation.Child, OnRefused = CrossTenantRefusalHandling.Report
        }, NodeExecutionIdentity.System));

        var ex = await Assert.ThrowsAsync<CrossTenantAccessDeniedException>(() =>
            new GetRtEntitiesByTypeNode(next, EtlContext, CkCache).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(reason, ex.ReasonCode);
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
