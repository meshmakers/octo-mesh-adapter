using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace MeshAdapter.Sdk.Tests.Common;

/// <summary>
///     AB#7462 — the shared guard for inserts of a type whose CK method replaces the generic create.
/// </summary>
public class GenericCreateGuardTests
{
    private const string Tenant = "tenant1";
    private const string Model = "Test-1.0.0";
    private const string AccountType = "Test/Account";
    private const string SpecialAccountType = "Test/SpecialAccount";
    private const string PlainType = "Test/Plain";
    private const string MethodName = "Create-1";

    private static readonly OctoObjectId PipelineRtId = OctoObjectId.GenerateNewId();

    private static readonly string ReplacingMethodId = new CkMethodGraph(new CkId<CkTypeId>($"{Model}/Account"),
        new CkMethodDto { MethodId = MethodName, Kind = CkMethodKindDto.Static, ReplacesGenericCreate = true })
        .QualifiedMethodId;

    private static RtCkId<CkTypeId> Rt(string id) => new(id);

    private static CkTypeGraph Graph(string rtId, bool withMethod)
    {
        var method = new CkMethodGraph(new CkId<CkTypeId>($"{Model}/Account"),
            new CkMethodDto { MethodId = MethodName, Kind = CkMethodKindDto.Static, ReplacesGenericCreate = true });
        return new CkTypeGraph(new CkId<CkTypeId>(rtId.Replace("Test/", $"{Model}/")),
            isAbstract: false, isFinal: false, isCollectionRoot: false, baseTypes: [],
            derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [], definedAttributes: [],
            allAttributes: new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>(),
            indexes: [], associations: new CkGraphDirectedAssociations([]), description: "test",
            enableChangeStreamPreAndPostImages: false,
            allMethods: withMethod ? new Dictionary<string, CkMethodGraph> { [MethodName] = method } : null);
    }

    /// <summary>Account declares the method, SpecialAccount inherits it (its graph holds it), Plain has none.</summary>
    private static ICkCacheService CkCache()
    {
        var cache = A.Fake<ICkCacheService>();
        foreach (var (type, flagged) in new[] { (AccountType, true), (SpecialAccountType, true), (PlainType, false) })
        {
            var graph = Graph(type, flagged);
            var graphOut = (CkTypeGraph?)graph;
            A.CallTo(() => cache.TryGetRtCkType(Tenant, Rt(type), out graphOut)).Returns(true)
                .AssignsOutAndRefParameters(graph);
        }

        return cache;
    }

    private static IMeshEtlContext Context(ExecutePipelineMethodInvocation? marker)
    {
        var context = A.Fake<IMeshEtlContext>();
        A.CallTo(() => context.TenantId).Returns(Tenant);
        A.CallTo(() => context.PipelineRtEntityId)
            .Returns(new RtEntityId("System.Communication/Pipeline", PipelineRtId));
        A.CallTo(() => context.MethodInvocation).Returns(marker);
        return context;
    }

    private static ExecutePipelineMethodInvocation Marker(string? methodId = null, string? pipelineRtId = null) =>
        new()
        {
            MethodId = methodId ?? ReplacingMethodId,
            InvocationId = Guid.NewGuid(),
            CkTypeId = AccountType,
            PipelineRtId = pipelineRtId ?? PipelineRtId.ToString()
        };

    private static EntityUpdateInfo<RtEntity> Insert(string type) =>
        EntityUpdateInfo<RtEntity>.CreateInsert(new RtEntity(Rt(type), OctoObjectId.GenerateNewId()));

    private static EntityUpdateInfo<RtEntity> Update(string type, EntityModOptions option)
    {
        var entity = new RtEntity(Rt(type), OctoObjectId.GenerateNewId());
        var id = new RtEntityId(Rt(type), entity.RtId);
        return option switch
        {
            EntityModOptions.Update => EntityUpdateInfo<RtEntity>.CreateUpdate(id, entity),
            EntityModOptions.Replace => EntityUpdateInfo<RtEntity>.CreateReplace(id, entity),
            _ => EntityUpdateInfo<RtEntity>.CreateDelete(id)
        };
    }

    private static GenericCreateReplacedByMethodException Refused(IMeshEtlContext context, ICkCacheService cache,
        params EntityUpdateInfo<RtEntity>[] infos) =>
        Assert.Throws<GenericCreateReplacedByMethodException>(() =>
            GenericCreateGuard.EnsureAllowed(context, cache, infos));

    [Fact]
    public void InsertOfFlaggedType_PassesWithMatchingMarker()
    {
        GenericCreateGuard.EnsureAllowed(Context(Marker()), CkCache(), [Insert(AccountType)]);
    }

    [Fact]
    public void InsertOfFlaggedType_IsRefusedWithoutMarker()
    {
        var ex = Refused(Context(null), CkCache(), Insert(AccountType));

        Assert.Equal("GENERIC_CREATE_REPLACED_BY_METHOD", ex.Code);
        Assert.Equal(GenericCreateReplacement.ReasonToken, ex.Code);
        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(Rt(AccountType).FullName, refusal.CkTypeId);
        Assert.Equal(ReplacingMethodId, refusal.MethodId);
        Assert.Equal(GenericCreateRefusalCause.MissingMarker, refusal.Cause);
        Assert.Contains(ReplacingMethodId, ex.Message);
    }

    [Fact]
    public void InsertOfFlaggedType_IsRefusedWhenTheMethodDiffers()
    {
        var ex = Refused(Context(Marker(methodId: "Test-1.0.0/Account/Other-1")), CkCache(), Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.MethodMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public void InsertOfFlaggedType_IsRefusedWhenTheMethodDiffersOnlyInCase()
    {
        var ex = Refused(Context(Marker(methodId: ReplacingMethodId.ToUpperInvariant())), CkCache(),
            Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.MethodMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public void InsertOfFlaggedType_IsRefusedWhenThePipelineDiffers()
    {
        var other = OctoObjectId.GenerateNewId().ToString();

        var ex = Refused(Context(Marker(pipelineRtId: other)), CkCache(), Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.PipelineMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public void InsertOfFlaggedType_IsRefusedWhenTheMarkerHasNoPipeline()
    {
        var ex = Refused(Context(Marker(pipelineRtId: string.Empty)), CkCache(), Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.PipelineMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public void InsertOfDerivedType_ResolvesTheInheritedMethod()
    {
        // The graph of SpecialAccount holds the inherited method; its qualified id is the declaring type's.
        GenericCreateGuard.EnsureAllowed(Context(Marker()), CkCache(), [Insert(SpecialAccountType)]);

        var ex = Refused(Context(null), CkCache(), Insert(SpecialAccountType));
        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(Rt(SpecialAccountType).FullName, refusal.CkTypeId);
        Assert.Equal(ReplacingMethodId, refusal.MethodId);
    }

    [Fact]
    public void InsertOfUnflaggedType_PassesWithoutMarker()
    {
        GenericCreateGuard.EnsureAllowed(Context(null), CkCache(), [Insert(PlainType)]);
    }

    [Fact]
    public void InsertOfUnknownType_IsNotRefused()
    {
        // not in the cache: the create fails on its own, the guard has nothing to say
        GenericCreateGuard.EnsureAllowed(Context(null), CkCache(), [Insert("Test/Unknown")]);
    }

    [Theory]
    [InlineData(EntityModOptions.Update)]
    [InlineData(EntityModOptions.Replace)]
    [InlineData(EntityModOptions.Delete)]
    public void NonInsertOperations_AreNotCheckedAndCostNoLookup(EntityModOptions option)
    {
        var cache = CkCache();

        GenericCreateGuard.EnsureAllowed(Context(null), cache, [Update(AccountType, option), Update(AccountType, option)]);

        CkTypeGraph? any;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._, out any)).MustNotHaveHappened();
    }

    [Fact]
    public void MixedBatch_IsRefusedAsAWhole()
    {
        var ex = Refused(Context(null), CkCache(), Insert(PlainType), Insert(PlainType), Insert(AccountType),
            Update(AccountType, EntityModOptions.Update), Insert(SpecialAccountType));

        Assert.Equal(2, ex.Refusals.Count);
        Assert.Equal([Rt(AccountType).FullName, Rt(SpecialAccountType).FullName], ex.Refusals.Select(r => r.CkTypeId));
    }

    [Fact]
    public void MixedBatch_WithMatchingMarker_PassesAsAWhole()
    {
        GenericCreateGuard.EnsureAllowed(Context(Marker()), CkCache(),
            [Insert(PlainType), Insert(AccountType), Insert(SpecialAccountType),
                Update(AccountType, EntityModOptions.Replace)]);
    }

    [Fact]
    public void LookupIsMemoizedPerType()
    {
        var cache = CkCache();
        var infos = Enumerable.Range(0, 50).Select(_ => Insert(AccountType))
            .Concat(Enumerable.Range(0, 50).Select(_ => Insert(PlainType))).ToList();

        GenericCreateGuard.EnsureAllowed(Context(Marker()), cache, infos);

        CkTypeGraph? any;
        A.CallTo(() => cache.TryGetRtCkType(Tenant, Rt(AccountType), out any)).MustHaveHappenedOnceExactly();
        A.CallTo(() => cache.TryGetRtCkType(Tenant, Rt(PlainType), out any)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void EmptyBatch_Passes()
    {
        GenericCreateGuard.EnsureAllowed(Context(null), CkCache(), []);
    }
}
