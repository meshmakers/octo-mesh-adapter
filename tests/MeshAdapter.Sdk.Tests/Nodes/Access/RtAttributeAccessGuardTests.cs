using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using static MeshAdapter.Sdk.Tests.Helpers.AttributeAccessTestModel;

namespace MeshAdapter.Sdk.Tests.Nodes.Access;

/// <summary>AB#7768 — the one implementation of "which attribute values may a pipeline read, filter on, write".</summary>
public class RtAttributeAccessGuardTests
{
    private readonly Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService _cache = Cache();
    private readonly IMeshEtlContext _context = A.Fake<IMeshEtlContext>();

    public RtAttributeAccessGuardTests()
    {
        ConfigureRun(_context, null);
    }

    private Task Write(params Meshmakers.Octo.Runtime.Contracts.IEntityUpdateInfo<RtEntity>[] updates) =>
        RtAttributeAccessGuard.EnsureWritesAllowedAsync(_context, _cache, updates);

    // ------------------------------------------------------------------ read

    [Fact]
    public void DropHidden_RemovesHiddenValue_KeepsMethodOnlyReadOnlyAndReadWrite()
    {
        var entity = Account(("Name", "n"), ("Created", "c"), ("Email", "e"), ("PasswordHash", "secret-hash"));

        var result = RtAttributeAccessGuard.DropHidden(_cache, Tenant, entity);

        Assert.False(result.Attributes.ContainsKey("PasswordHash"));
        Assert.Equal("n", result.Attributes["Name"]);
        Assert.Equal("c", result.Attributes["Created"]);
        Assert.Equal("e", result.Attributes["Email"]);
    }

    [Fact]
    public void DropHidden_NeverModifiesTheOriginalEntity_AndKeepsSystemProperties()
    {
        var entity = Account(("Name", "n"), ("PasswordHash", "h"));

        var result = RtAttributeAccessGuard.DropHidden(_cache, Tenant, entity);

        Assert.NotSame(entity, result);
        Assert.True(entity.Attributes.ContainsKey("PasswordHash"));
        Assert.Equal(entity.RtId, result.RtId);
        Assert.Equal(entity.CkTypeId, result.CkTypeId);
        Assert.Equal("wkn", result.RtWellKnownName);
        Assert.Equal(7UL, result.RtVersion);
        Assert.Equal("creator", result.RtCreatedBy);
    }

    [Fact]
    public void DropHidden_CopiesEveryPublicSettableProperty_OfRtEntity()
    {
        // Drift guard: a property added to RtEntity must be copied by DropHidden.
        var entity = Account(("PasswordHash", "h"));
        entity.RtCreationDateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        entity.RtChangedDateTime = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc);
        entity.RtArchivedDateTime = new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc);
        entity.RtDisplayName = "dn";
        entity.RtDisplayDescription = "dd";

        var result = RtAttributeAccessGuard.DropHidden(_cache, Tenant, entity);

        foreach (var property in typeof(RtEntity).GetProperties()
                     .Where(p => p.SetMethod is { IsPublic: true } && p.Name != nameof(RtEntity.Attributes)))
        {
            Assert.True(Equals(property.GetValue(entity), property.GetValue(result)),
                $"RtEntity.{property.Name} is not copied by DropHidden");
        }
    }

    [Fact]
    public void DropHidden_ReturnsTheSameInstance_WhenNothingIsHidden()
    {
        var entity = Account(("Name", "n"), ("Email", "e"));

        Assert.Same(entity, RtAttributeAccessGuard.DropHidden(_cache, Tenant, entity));
    }

    [Fact]
    public void DropHidden_RemovesAHiddenRecordMember_AndRecordArrayMember()
    {
        var entity = Account(
            ("Profile", Credential(("Label", "l"), ("Token", "t"))),
            ("Credentials", new List<object> { Credential(("Label", "a"), ("Token", "t1")), Credential(("Label", "b")) }));

        var result = RtAttributeAccessGuard.DropHidden(_cache, Tenant, entity);

        var profile = Assert.IsType<RtRecord>(result.Attributes["Profile"]);
        Assert.Equal("l", profile.Attributes["Label"]);
        Assert.False(profile.Attributes.ContainsKey("Token"));
        var items = Assert.IsAssignableFrom<IEnumerable<object>>(result.Attributes["Credentials"]).Cast<RtRecord>().ToList();
        Assert.Equal(2, items.Count);
        Assert.All(items, i => Assert.False(i.Attributes.ContainsKey("Token")));
        Assert.Equal("a", items[0].Attributes["Label"]);
        // original untouched
        Assert.True(((RtRecord)entity.Attributes["Profile"]!).Attributes.ContainsKey("Token"));
    }

    [Fact]
    public void DropHidden_LeavesAnUnknownType_Alone()
    {
        var entity = Entity(new RtCkId<CkTypeId>("Test-1.0.0/Unknown"), ("PasswordHash", "h"));

        Assert.Same(entity, RtAttributeAccessGuard.DropHidden(_cache, Tenant, entity));
    }

    [Fact]
    public async Task EnsureCacheLoaded_LoadsAnUnloadedCache_AndFailsClosedWhenItStaysUnloaded()
    {
        var cache = A.Fake<Meshmakers.Octo.ConstructionKit.Contracts.Services.ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(false);

        var ex = await Assert.ThrowsAsync<RtAttributeAccessCheckUnavailableException>(() =>
            RtAttributeAccessGuard.EnsureCacheLoadedAsync(_context, cache));

        Assert.Equal("RT_ATTRIBUTE_ACCESS_CHECK_UNAVAILABLE", ex.Code);
    }

    // ---------------------------------------------------------- filter / sort

    [Theory]
    [InlineData("PasswordHash")]
    [InlineData("passwordhash")]
    [InlineData("Profile.Token")]
    [InlineData("Credentials[*].Token")]
    [InlineData("Credentials")] // whole record array contains a Hidden sub-attribute
    public void EnsureNotHidden_RefusesAPathThatReachesAHiddenAttribute(string path)
    {
        var ex = Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureNotHidden(_cache, Tenant, "$.nodes[0]", [AccountType], [path],
                "field filter"));

        Assert.Equal("RT_ATTRIBUTE_HIDDEN", ex.Code);
        Assert.Equal("$.nodes[0]", ex.Node);
        Assert.Equal(path, ex.AttributePath);
        Assert.Contains("$.nodes[0]", ex.Message);
        Assert.Contains(path, ex.Message);
    }

    [Theory]
    [InlineData("Name")]
    [InlineData("NormalizedEmail")]
    [InlineData("Email")] // MethodOnly stays filterable
    [InlineData("Created")] // ReadOnly stays filterable
    [InlineData("Profile.Label")]
    [InlineData("RtId")]
    [InlineData("RtWellKnownName")]
    public void EnsureNotHidden_AllowsEverythingElse(string path)
    {
        RtAttributeAccessGuard.EnsureNotHidden(_cache, Tenant, "n", [AccountType], [path], "field filter");
    }

    [Fact]
    public void EnsureNotHidden_ANavigatedPath_IsJudgedByItsTrailingAttributeOnEveryType()
    {
        Assert.Throws<RtAttributeHiddenException>(() =>
            RtAttributeAccessGuard.EnsureNotHidden(_cache, Tenant, "n", [PlainType],
                ["parent.Test/Account->passwordHash"], "query column"));
        RtAttributeAccessGuard.EnsureNotHidden(_cache, Tenant, "n", [PlainType],
            ["parent.Test/Account->name"], "query column");
    }

    // ----------------------------------------------------------------- write

    [Fact]
    public async Task Write_HiddenAttribute_IsRefused_EvenInAMethodRun()
    {
        ConfigureRun(_context, MethodRunMarker());

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write(Update(("PasswordHash", "x"))));

        Assert.Equal("RT_ATTRIBUTE_WRITE_RESTRICTED", ex.Code);
        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal("PasswordHash", refusal.AttributePath);
        Assert.Equal(RtAttributeWriteRefusalCause.Hidden, refusal.Cause);
        Assert.DoesNotContain("x'", ex.Message);
    }

    [Fact]
    public async Task Write_ReadOnlyAttribute_IsRefused_OnInsertUpdateAndInAMethodRun()
    {
        ConfigureRun(_context, MethodRunMarker());

        foreach (var update in new[] { Insert(("Created", "c")), Update(("Created", "c")) })
        {
            var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(update));
            Assert.Equal(RtAttributeWriteRefusalCause.ReadOnly, Assert.Single(ex.Refusals).Cause);
        }
    }

    [Fact]
    public async Task Write_MethodOnlyAttribute_IsRefusedWithoutAMarker()
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(Update(("Email", "e"))));

        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal("Email", refusal.AttributePath);
        Assert.Equal(RtAttributeWriteRefusalCause.MethodOnlyMissingMarker, refusal.Cause);
    }

    [Fact]
    public async Task Write_MethodOnlyAttribute_IsAllowedInTheMethodRunOfTheType()
    {
        ConfigureRun(_context, MethodRunMarker());

        await Write(Update(("Email", "e"), ("Name", "n")));
        await Write(Insert(("Email", "e")));
    }

    [Fact]
    public async Task Write_MethodOnlyAttribute_IsRefusedForAMethodOfAnotherType()
    {
        ConfigureRun(_context, MethodRunMarker() with { MethodId = "Test/Other.DoIt-1" });

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(Update(("Email", "e"))));

        Assert.Equal(RtAttributeWriteRefusalCause.MethodOnlyMethodMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public async Task Write_MethodOnlyAttribute_IsRefusedForTheMarkerOfAnotherPipeline()
    {
        ConfigureRun(_context, MethodRunMarker() with { PipelineRtId = OctoObjectId.GenerateNewId().ToString() });

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(Update(("Email", "e"))));

        Assert.Equal(RtAttributeWriteRefusalCause.MethodOnlyPipelineMismatch, Assert.Single(ex.Refusals).Cause);
    }

    [Fact]
    public async Task Write_MethodOnlyAttribute_OfATypeWithoutThatMethod_IsRefusedEvenInAMethodRun()
    {
        // Plain has no method and no MethodOnly attribute; a marker for Account's method must not matter for it,
        // but a MethodOnly attribute on a type without the method is refused.
        ConfigureRun(_context, MethodRunMarker() with { MethodId = "Test/Plain.SetEmail-1" });

        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(Update(("Email", "e"))));
    }

    [Fact]
    public async Task Write_HiddenRecordMember_IsRefused()
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write(Update(("Profile", Credential(("Label", "l"), ("Token", "t"))))));

        Assert.Equal("Profile.Token", Assert.Single(ex.Refusals).AttributePath);
    }

    [Fact]
    public async Task Write_RecordArrayItemUpdate_OfAHiddenMember_IsRefused()
    {
        var entity = Account();
        var update = EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(
            new RtEntityId(AccountType, entity.RtId), entity,
            [new RtRecordArrayItemUpdate("Credentials", "Label", "a",
                new Dictionary<string, object?> { ["Token"] = "t" })]);

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(update));

        Assert.Equal("Credentials.Token", Assert.Single(ex.Refusals).AttributePath);
    }

    [Fact]
    public async Task Write_ReadWriteAttributes_AndUnknownTypes_AndDeletes_AreNotAffected()
    {
        await Write(Insert(("Name", "n"), ("NormalizedEmail", "a@b")),
            Update(("Name", "n"), ("Profile", Credential(("Label", "l")))));
        await Write(EntityUpdateInfo<RtEntity>.CreateInsert(new RtCkId<CkTypeId>("Test-1.0.0/Unknown"),
            Entity(new RtCkId<CkTypeId>("Test-1.0.0/Unknown"), ("PasswordHash", "h"))));
        await Write(EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(AccountType, OctoObjectId.GenerateNewId())));
    }

    [Fact]
    public async Task Write_Replace_OfATypeWithAttributesThisRunMayNotWrite_IsRefused_BecauseItWouldClearThem()
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write(Replace(("Name", "n"))));

        Assert.Contains(ex.Refusals, r =>
            r.AttributePath == "PasswordHash" && r.Cause == RtAttributeWriteRefusalCause.ReplaceWouldClear);
        Assert.Contains(ex.Refusals, r =>
            r.AttributePath == "Email" && r.Cause == RtAttributeWriteRefusalCause.ReplaceWouldClear);
    }

    [Fact]
    public async Task Write_TheWholeBatchIsRefused_AndAllRefusalsAreListed()
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write(Update(("Name", "n")), Update(("Email", "e")), Insert(("PasswordHash", "h"))));

        Assert.Equal(2, ex.Refusals.Count);
    }

    [Fact]
    public async Task Write_RunsTheReplacedCreateGuardFirst()
    {
        var cache = ReplacedCreateTestSupport.CkCache();

        await Assert.ThrowsAsync<GenericCreateReplacedByMethodException>(() =>
            RtAttributeAccessGuard.EnsureWritesAllowedAsync(_context, cache,
                [ReplacedCreateTestSupport.Insert(ReplacedCreateTestSupport.ReplacedType)]));
    }
}
