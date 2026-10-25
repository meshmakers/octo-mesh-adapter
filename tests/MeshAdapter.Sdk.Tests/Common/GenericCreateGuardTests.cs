using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
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
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
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

    private static Task<GenericCreateReplacedByMethodException> Refused(IMeshEtlContext context, ICkCacheService cache,
        params EntityUpdateInfo<RtEntity>[] infos) =>
        Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            GenericCreateGuard.EnsureAllowedAsync(context, cache, infos));

    [Fact]
    public async Task InsertOfFlaggedType_PassesWithMatchingMarker()
    {
        await GenericCreateGuard.EnsureAllowedAsync(Context(Marker()), CkCache(), [Insert(AccountType)]);
    }

    [Fact]
    public async Task InsertOfFlaggedType_IsRefusedWithoutMarker()
    {
        var ex = await Refused(Context(null), CkCache(), Insert(AccountType));

        Assert.Equal("GENERIC_CREATE_REPLACED_BY_METHOD", ex.Code);
        Assert.Equal(GenericCreateReplacement.ReasonToken, ex.Code);
        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(Rt(AccountType).FullName, refusal.CkTypeId);
        Assert.Equal(ReplacingMethodId, refusal.MethodId);
        Assert.Equal(GenericCreateRefusalCause.MissingMarker, refusal.Cause);
        Assert.Contains(ReplacingMethodId, ex.Message);
    }

    [Fact]
    public async Task InsertOfFlaggedType_IsRefusedWhenTheMethodDiffers()
    {
        var ex = await Refused(Context(Marker(methodId: "Test-1.0.0/Account/Other-1")), CkCache(), Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.MethodMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public async Task InsertOfFlaggedType_IsRefusedWhenTheMethodDiffersOnlyInCase()
    {
        var ex = await Refused(Context(Marker(methodId: ReplacingMethodId.ToUpperInvariant())), CkCache(),
            Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.MethodMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public async Task InsertOfFlaggedType_IsRefusedWhenThePipelineDiffers()
    {
        var other = OctoObjectId.GenerateNewId().ToString();

        var ex = await Refused(Context(Marker(pipelineRtId: other)), CkCache(), Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.PipelineMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public async Task InsertOfFlaggedType_IsRefusedWhenTheMarkerHasNoPipeline()
    {
        var ex = await Refused(Context(Marker(pipelineRtId: string.Empty)), CkCache(), Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.PipelineMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public async Task InsertOfDerivedType_ResolvesTheInheritedMethod()
    {
        // The graph of SpecialAccount holds the inherited method; its qualified id is the declaring type's.
        await GenericCreateGuard.EnsureAllowedAsync(Context(Marker()), CkCache(), [Insert(SpecialAccountType)]);

        var ex = await Refused(Context(null), CkCache(), Insert(SpecialAccountType));
        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(Rt(SpecialAccountType).FullName, refusal.CkTypeId);
        Assert.Equal(ReplacingMethodId, refusal.MethodId);
    }

    [Fact]
    public async Task InsertOfUnflaggedType_PassesWithoutMarker()
    {
        await GenericCreateGuard.EnsureAllowedAsync(Context(null), CkCache(), [Insert(PlainType)]);
    }

    [Fact]
    public async Task InsertOfUnknownType_IsNotRefused()
    {
        // not in the cache: the create fails on its own, the guard has nothing to say
        await GenericCreateGuard.EnsureAllowedAsync(Context(null), CkCache(), [Insert("Test/Unknown")]);
    }

    [Theory]
    [InlineData(EntityModOptions.Update)]
    [InlineData(EntityModOptions.Replace)]
    [InlineData(EntityModOptions.Delete)]
    public async Task NonInsertOperations_AreNotCheckedAndCostNoLookup(EntityModOptions option)
    {
        var cache = CkCache();

        await GenericCreateGuard.EnsureAllowedAsync(Context(null), cache, [Update(AccountType, option), Update(AccountType, option)]);

        CkTypeGraph? any;
        A.CallTo(() => cache.TryGetRtCkType(A<string>._, A<RtCkId<CkTypeId>>._, out any)).MustNotHaveHappened();
    }

    [Fact]
    public async Task MixedBatch_IsRefusedAsAWhole()
    {
        var ex = await Refused(Context(null), CkCache(), Insert(PlainType), Insert(PlainType), Insert(AccountType),
            Update(AccountType, EntityModOptions.Update), Insert(SpecialAccountType));

        Assert.Equal(2, ex.Refusals.Count);
        Assert.Equal([Rt(AccountType).FullName, Rt(SpecialAccountType).FullName], ex.Refusals.Select(r => r.CkTypeId));
    }

    [Fact]
    public async Task MixedBatch_WithMatchingMarker_PassesAsAWhole()
    {
        await GenericCreateGuard.EnsureAllowedAsync(Context(Marker()), CkCache(),
            [Insert(PlainType), Insert(AccountType), Insert(SpecialAccountType),
                Update(AccountType, EntityModOptions.Replace)]);
    }

    [Fact]
    public async Task LookupIsMemoizedPerType()
    {
        var cache = CkCache();
        var infos = Enumerable.Range(0, 50).Select(_ => Insert(AccountType))
            .Concat(Enumerable.Range(0, 50).Select(_ => Insert(PlainType))).ToList();

        await GenericCreateGuard.EnsureAllowedAsync(Context(Marker()), cache, infos);

        CkTypeGraph? any;
        A.CallTo(() => cache.TryGetRtCkType(Tenant, Rt(AccountType), out any)).MustHaveHappenedOnceExactly();
        A.CallTo(() => cache.TryGetRtCkType(Tenant, Rt(PlainType), out any)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EmptyBatch_Passes()
    {
        await GenericCreateGuard.EnsureAllowedAsync(Context(null), CkCache(), []);
    }

    // ---- tenant CK cache not loaded (unloaded mid-run by a lease leave / CkModelChanged) --------------------------

    private static (IMeshEtlContext Context, ITenantRepository Repository, ICkCacheService Cache) UnloadedTenant(
        ExecutePipelineMethodInvocation? marker, bool loadWorks)
    {
        var cache = CkCache();
        var loaded = false;
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).ReturnsLazily(() => loaded);

        var repository = A.Fake<ITenantRepository>();
        A.CallTo(() => repository.LoadCacheForTenantAsync(cache))
            .Invokes(() => loaded = loadWorks)
            .Returns(Task.CompletedTask);

        var context = Context(marker);
        A.CallTo(() => context.TenantRepository).Returns(repository);
        return (context, repository, cache);
    }

    [Fact]
    public async Task UnloadedTenant_IsLoadedFirst_AndTheInsertIsRefusedWithoutMarker()
    {
        var (context, repository, cache) = UnloadedTenant(null, loadWorks: true);

        var ex = await Refused(context, cache, Insert(AccountType));

        Assert.Equal(GenericCreateRefusalCause.MissingMarker, Assert.Single(ex.Refusals).Cause);
        A.CallTo(() => repository.LoadCacheForTenantAsync(cache)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UnloadedTenant_IsLoadedFirst_AndTheInsertPassesWithMatchingMarker()
    {
        var (context, repository, cache) = UnloadedTenant(Marker(), loadWorks: true);

        await GenericCreateGuard.EnsureAllowedAsync(context, cache, [Insert(AccountType), Insert(SpecialAccountType)]);

        A.CallTo(() => repository.LoadCacheForTenantAsync(cache)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task UnloadedTenant_ThatStaysUnloaded_FailsClosed()
    {
        var (context, _, cache) = UnloadedTenant(Marker(), loadWorks: false);

        var ex = await Assert.ThrowsAsync<GenericCreateCheckUnavailableException>(() =>
            GenericCreateGuard.EnsureAllowedAsync(context, cache, [Insert(PlainType)]));

        Assert.Equal("GENERIC_CREATE_CHECK_UNAVAILABLE", ex.Code);
        Assert.Contains(Tenant, ex.Message);
    }

    [Fact]
    public async Task UnloadedTenant_WhoseLoadFails_FailsClosedAndKeepsTheCause()
    {
        var (context, repository, cache) = UnloadedTenant(Marker(), loadWorks: true);
        var failure = new InvalidOperationException("mongo down");
        A.CallTo(() => repository.LoadCacheForTenantAsync(cache)).ThrowsAsync(failure);

        var ex = await Assert.ThrowsAsync<GenericCreateCheckUnavailableException>(() =>
            GenericCreateGuard.EnsureAllowedAsync(context, cache, [Insert(AccountType)]));

        Assert.Same(failure, ex.InnerException);
    }

    [Fact]
    public async Task UnloadedTenant_IsNotLoaded_WhenTheBatchHasNoInsert()
    {
        var (context, repository, cache) = UnloadedTenant(null, loadWorks: true);

        await GenericCreateGuard.EnsureAllowedAsync(context, cache,
            [Update(AccountType, EntityModOptions.Update), Update(AccountType, EntityModOptions.Replace)]);

        A.CallTo(() => repository.LoadCacheForTenantAsync(A<ICkCacheService>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task LoadedTenant_IsNotLoadedAgain()
    {
        var cache = CkCache();
        var repository = A.Fake<ITenantRepository>();
        var context = Context(Marker());
        A.CallTo(() => context.TenantRepository).Returns(repository);

        await GenericCreateGuard.EnsureAllowedAsync(context, cache, [Insert(AccountType)]);

        A.CallTo(() => repository.LoadCacheForTenantAsync(A<ICkCacheService>._)).MustNotHaveHappened();
    }
}
