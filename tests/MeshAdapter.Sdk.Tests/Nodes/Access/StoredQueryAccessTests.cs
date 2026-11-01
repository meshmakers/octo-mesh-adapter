using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using static MeshAdapter.Sdk.Tests.Helpers.AttributeAccessTestModel;

namespace MeshAdapter.Sdk.Tests.Nodes.Access;

/// <summary>
///     AB#8049 — a stored query whose OWN field filter, sort order, attribute-search path, group-by or aggregation
///     column reaches a Hidden attribute is refused before the query reaches the repository: the number of returned
///     rows would answer a guess of the hidden value (password-hash oracle). Covers
///     <see cref="RtAttributeAccessGuard.EnsureStoredQueryNotHidden" /> and its use in <c>GetQueryById@1</c>; the
///     notification engine is covered against a real MongoDB in
///     <c>IntegrationTests/Nodes/Access/StoredQueryHiddenAttributeIntegrationTests</c>.
/// </summary>
public class StoredQueryAccessTests : SessionNodeTestBase
{
    private const string Guess = "dummy-guess-0001";
    private const string Node = "Test/GetQueryById";

    private readonly ICkCacheService _cache = Cache();

    public StoredQueryAccessTests()
    {
        ConfigureRun(EtlContext, null);
    }

    private static AttributeRecordValueList<RtFieldFilterRecord> Filter(string path) =>
    [
        new() { AttributePath = path, Operator = RtFieldFilterOperatorEnum.Equals, ComparisonValue = Guess }
    ];

    private static AttributeRecordValueList<RtSortOrderItemRecord> Sort(string path) =>
        [new() { AttributePath = path, SortOrder = RtSortOrdersEnum.Ascending }];

    private static AttributeRecordValueList<RtAggregationQueryColumnRecord> Aggregate(string path) =>
        [new() { AttributePath = path, AggregationType = RtAggregationTypesEnum.Count }];

    public static TheoryData<string, string> HiddenStoredQueries() => new()
    {
        { "simple-filter", "stored field filter" },
        { "simple-filter-record-member", "stored field filter" },
        { "simple-filter-case", "stored field filter" },
        { "simple-sort", "stored sort order" },
        { "simple-search", "stored attribute search" },
        { "simple-column", "stored query column" },
        { "aggregation-filter", "stored field filter" },
        { "aggregation-column", "stored aggregation column" },
        { "grouping-filter", "stored field filter" },
        { "grouping-groupby", "stored group-by column" },
        { "sd-simple-filter", "stored field filter" },
        { "sd-simple-sort", "stored sort order" },
        { "sd-grouping-groupby", "stored group-by column" }
    };

    private static RtPersistentQuery Query(string shape) => shape switch
    {
        "simple-filter" => new RtSimpleRtQuery
            { QueryCkTypeId = "Test/Account", Columns = new AttributeStringValueList(["name"]), FieldFilter = Filter("PasswordHash") },
        "simple-filter-record-member" => new RtSimpleRtQuery
            { QueryCkTypeId = "Test/Account", Columns = new AttributeStringValueList(["name"]), FieldFilter = Filter("Credentials.Token") },
        "simple-filter-case" => new RtSimpleRtQuery
            { QueryCkTypeId = "Test/Account", Columns = new AttributeStringValueList(["name"]), FieldFilter = Filter("passwordHash") },
        "simple-sort" => new RtSimpleRtQuery
            { QueryCkTypeId = "Test/Account", Columns = new AttributeStringValueList(["name"]), Sorting = Sort("PasswordHash") },
        "simple-search" => new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Account", Columns = new AttributeStringValueList(["name"]),
            AttributeSearchFilter = new RtAttributeSearchFilterRecord
                { AttributePaths = new AttributeStringValueList(["name", "passwordHash"]), SearchValue = Guess }
        },
        "simple-column" => new RtSimpleRtQuery
            { QueryCkTypeId = "Test/Account", Columns = new AttributeStringValueList(["name", "passwordHash"]) },
        "aggregation-filter" => new RtAggregationRtQuery
            { QueryCkTypeId = "Test/Account", Columns = Aggregate("name"), FieldFilter = Filter("PasswordHash") },
        "aggregation-column" => new RtAggregationRtQuery
            { QueryCkTypeId = "Test/Account", Columns = Aggregate("passwordHash") },
        "grouping-filter" => new RtGroupingAggregationRtQuery
        {
            QueryCkTypeId = "Test/Account", GroupingColumns = new AttributeStringValueList(["name"]),
            Columns = Aggregate("name"), FieldFilter = Filter("PasswordHash")
        },
        "grouping-groupby" => new RtGroupingAggregationRtQuery
        {
            QueryCkTypeId = "Test/Account", GroupingColumns = new AttributeStringValueList(["passwordHash"]),
            Columns = Aggregate("name")
        },
        "sd-simple-filter" => new RtSimpleSdQuery
        {
            QueryCkTypeId = "Test/Account", ArchiveRtId = OctoObjectId.GenerateNewId().ToString(),
            Columns = new AttributeStringValueList(["name"]), FieldFilter = Filter("PasswordHash")
        },
        "sd-simple-sort" => new RtSimpleSdQuery
        {
            QueryCkTypeId = "Test/Account", ArchiveRtId = OctoObjectId.GenerateNewId().ToString(),
            Columns = new AttributeStringValueList(["name"]), Sorting = Sort("PasswordHash")
        },
        "sd-grouping-groupby" => new RtGroupingAggregationSdQuery
        {
            QueryCkTypeId = "Test/Account", ArchiveRtId = OctoObjectId.GenerateNewId().ToString(),
            GroupingColumns = new AttributeStringValueList(["passwordHash"]), Columns = Aggregate("name")
        },
        _ => throw new ArgumentOutOfRangeException(nameof(shape))
    };

    [Theory]
    [MemberData(nameof(HiddenStoredQueries))]
    public void Guard_RefusesEveryStoredPathOnAHiddenAttribute_WithoutTheValue(string shape, string usage)
    {
        var ex = Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureStoredQueryNotHidden(_cache, Tenant, Node, Query(shape)));

        Assert.Equal(usage, ex.Usage);
        Assert.Equal(RtAttributeAccessCodes.HiddenAttribute, ex.Code);
        Assert.DoesNotContain(Guess, ex.Message);
    }

    [Fact]
    public void Guard_AStoredQueryOnVisibleAttributes_Passes()
    {
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Account",
            Columns = new AttributeStringValueList(["name", "created", "email", "credentials.label"]),
            FieldFilter = Filter("NormalizedEmail"),
            Sorting = Sort("Name")
        };

        RtAttributeAccessGuard.EnsureStoredQueryNotHidden(_cache, Tenant, Node, query);
    }

    [Fact]
    public void Guard_AnEntitySelectorKeyOnAHiddenAttribute_IsRefused_AndTheSelectorValueIsNotEchoed()
    {
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Plain",
            Columns = new AttributeStringValueList([$"members.Test/Account[passwordHash={Guess}]->name"])
        };

        var ex = Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureStoredQueryNotHidden(_cache, Tenant, Node, query));

        Assert.Equal("PasswordHash", ex.HiddenAttribute);
        Assert.DoesNotContain(Guess, ex.Message);
        Assert.DoesNotContain(Guess, ex.AttributePath);
    }

    [Fact]
    public void Guard_AnEntitySelectorKeyOnAVisibleAttribute_Passes()
    {
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Plain",
            Columns = new AttributeStringValueList(["members.Test/Account[name=x]->name"])
        };

        RtAttributeAccessGuard.EnsureStoredQueryNotHidden(_cache, Tenant, Node, query);
    }

    [Fact]
    public void Guard_AQueryTypeTheCacheDoesNotKnow_IsCheckedAgainstEveryType()
    {
        // e.g. a CK interface id or a type of a model that is not loaded: fail closed.
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Named", Columns = new AttributeStringValueList(["name"]),
            FieldFilter = Filter("PasswordHash")
        };

        Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureStoredQueryNotHidden(_cache, Tenant, Node, query));
    }

    [Fact]
    public void Guard_AFilterOnAnAttributeThatOnlyADerivedTypeHides_IsRefused()
    {
        // The query type is a base type; the repository returns derived entities too, so a derived type's Hidden
        // attribute is an oracle as well.
        var cache = DerivedTypeCache();
        var query = new RtSimpleRtQuery
        {
            QueryCkTypeId = "Test/Base", Columns = new AttributeStringValueList(["name"]),
            FieldFilter = Filter("PasswordHash")
        };

        Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureStoredQueryNotHidden(cache, Tenant, Node, query));
    }

    private static ICkCacheService DerivedTypeCache()
    {
        var cache = A.Fake<ICkCacheService>();
        var baseGraph = BaseTypeWithDerivedAccount();
        var baseOut = (CkTypeGraph?)baseGraph;
        var account = AccountGraph();
        var accountOut = (CkTypeGraph?)account;
        A.CallTo(() => cache.TryGetRtCkType(Tenant, new RtCkId<CkTypeId>("Test/Base"), out baseOut)).Returns(true)
            .AssignsOutAndRefParameters(baseGraph);
        A.CallTo(() => cache.TryGetRtCkType(Tenant, AccountType, out accountOut)).Returns(true)
            .AssignsOutAndRefParameters(account);
        A.CallTo(() => cache.GetRtCkType(Tenant, AccountType)).Returns(account);
        A.CallTo(() => cache.GetRtCkType(Tenant, new RtCkId<CkTypeId>("Test/Base"))).Returns(baseGraph);
        A.CallTo(() => cache.GetCkTypes(Tenant)).Returns(new List<CkTypeGraph> { baseGraph, account });
        return cache;
    }

    [Fact]
    public void Guard_ANodeFilterOnAnAttributeThatOnlyADerivedTypeHides_IsRefused()
    {
        // The node's own filters are judged against the same type set as the stored query (review of PR #99).
        var cache = DerivedTypeCache();

        Assert.Throws<RtAttributeHiddenException>(() => RtAttributeAccessGuard.EnsureNotHiddenForQueryType(cache,
            Tenant, Node, "Test/Base", ["PasswordHash"], "field filter"));
        RtAttributeAccessGuard.EnsureNotHiddenForQueryType(cache, Tenant, Node, "Test/Base", ["Name"], "field filter");
    }

    [Theory]
    [MemberData(nameof(HiddenStoredQueries))]
    public async Task GetQueryById_AStoredQueryReachingAHiddenAttribute_FailsTheNode_BeforeAnyQuery(string shape,
        string usage)
    {
        A.CallTo(() => TenantRepository.GetRtEntityByRtIdAsync<RtPersistentQuery>(A<IOctoSession>._, A<OctoObjectId>._))
            .Returns(Task.FromResult<RtPersistentQuery?>(Query(shape)));
        var systemContext = A.Fake<ISystemContext>();
        var (dataContext, nodeContext, next) = PrepareTest(new GetQueryByIdNodeConfiguration
        {
            QueryRtId = OctoObjectId.GenerateNewId(), TargetPath = "$.queryResult"
        });

        var ex = await Assert.ThrowsAsync<RtAttributeHiddenException>(() =>
            new GetQueryByIdNode(next, EtlContext, _cache, systemContext).ProcessObjectAsync(dataContext, nodeContext));

        Assert.Equal(usage, ex.Usage);
        Assert.DoesNotContain(Guess, ex.Message);
        A.CallTo(TenantRepository).Where(c => c.Method.Name.StartsWith("GetRtEntitiesGraph", StringComparison.Ordinal))
            .MustNotHaveHappened();
        // Stream-data queries never reach the stream-data repository either.
        A.CallTo(systemContext).MustNotHaveHappened();
    }

    private static CkTypeGraph BaseTypeWithDerivedAccount() => new(
        new CkId<CkTypeId>($"{Model}/Base"), isAbstract: false, isFinal: false, isCollectionRoot: false,
        baseTypes: [], derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null,
        derivedTypes: [new CkGraphTypeInheritance(AccountGraph().CkTypeId, new CkId<CkTypeId>($"{Model}/Base"), 1)],
        definedAttributes: [],
        allAttributes: new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(), indexes: [],
        associations: new CkGraphDirectedAssociations([]), description: "test",
        enableChangeStreamPreAndPostImages: false, allMethods: null);
}
