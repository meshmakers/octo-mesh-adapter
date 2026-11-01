using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;

namespace MeshAdapter.Sdk.Tests.Nodes.Access;

/// <summary>
///     AB#8059 - the pipeline write guard enforces the engine's <see cref="IdentityOwnedWrites" /> list: the identity-owned
///     attributes of <c>System.Identity/User</c> and the edges of the identity association roles are refused on insert,
///     update, replace and association insert/delete, except in the method run of a System.Identity type. Synthetic graph
///     modelled on System.Identity (version-qualified model ids, as in the cache); the integration suite
///     <c>IdentityCredentialGuardPipelineTests</c> runs the same rules against the real model.
/// </summary>
public class IdentityOwnedWriteGuardTests
{
    private const string Tenant = ReplacedCreateTestSupport.Tenant;
    private const string IdentityModel = "System.Identity-2.30.0";

    private static readonly RtCkId<CkTypeId> UserType = new("System.Identity/User");
    private static readonly RtCkId<CkTypeId> RoleType = new("System.Identity/Role");
    private static readonly RtCkId<CkTypeId> PlainType = new("Test/Plain");

    private static readonly string[] OwnedReadWrite =
    [
        "NormalizedUserName", "NormalizedEmail", "EmailConfirmed", "PhoneNumberConfirmed", "TwoFactorEnabled",
        "LockoutEnabled", "ResetPasswordOnLogin", "Claims", "UserLogins", "PreferredChannelBindingId"
    ];

    private readonly ICkCacheService _cache = Cache();
    private readonly IMeshEtlContext _context = A.Fake<IMeshEtlContext>();

    public IdentityOwnedWriteGuardTests()
    {
        ReplacedCreateTestSupport.ConfigureRun(_context, null);
    }

    public static TheoryData<string> OwnedAttributes() => new(OwnedReadWrite);

    public static TheoryData<string> IdentityRoles() => new(IdentityOwnedWrites.AssociationRoleIds.Select(r => r.FullName));

    // ---------------------------------------------------------------------------------------------- attributes

    [Theory]
    [MemberData(nameof(OwnedAttributes))]
    public async Task Update_OfAnIdentityOwnedUserAttribute_IsRefused(string attribute)
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([Update(UserType, ("FirstName", "f"), (attribute, "x"))]));

        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(attribute, refusal.AttributePath);
        Assert.Equal(RtAttributeWriteRefusalCause.IdentityOwned, refusal.Cause);
        Assert.Equal("RT_ATTRIBUTE_WRITE_RESTRICTED", ex.Code);
        Assert.DoesNotContain("'x'", ex.Message);
    }

    [Theory]
    [MemberData(nameof(OwnedAttributes))]
    public async Task Insert_OfAUser_WithAnIdentityOwnedAttribute_IsRefused(string attribute)
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([EntityUpdateInfo<RtEntity>.CreateInsert(UserType, Entity(UserType, ("FirstName", "f"), (attribute, "x")))]));

        Assert.Equal(RtAttributeWriteRefusalCause.IdentityOwned, Assert.Single(ex.Refusals).Cause);
    }

    [Theory]
    [InlineData("LockoutEnd")]
    [InlineData("AccessFailedCount")]
    public async Task Update_OfALockoutField_IsRefusedOnce_AsReadOnly(string attribute)
    {
        // ReadOnly in the model and on the identity list: one refusal per path, the access cause wins.
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([Update(UserType, (attribute, "0"))]));

        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(RtAttributeWriteRefusalCause.ReadOnly, refusal.Cause);
    }

    [Fact]
    public async Task RecordArrayItemUpdate_OfTheClaims_IsRefused()
    {
        var entity = Entity(UserType);
        var update = EntityUpdateInfo<RtEntity>.CreateRecordArrayItemUpdate(new RtEntityId(UserType, entity.RtId), entity,
            [new RtRecordArrayItemUpdate("Claims", "Type", "role", new Dictionary<string, object?> { ["Value"] = "admin" })]);

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write([update]));

        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal("Claims", refusal.AttributePath);
        Assert.Equal(RtAttributeWriteRefusalCause.IdentityOwned, refusal.Cause);
    }

    [Fact]
    public async Task Replace_OfAUser_IsRefused_BecauseItWouldClearTheIdentityOwnedAttributes()
    {
        var replace = Entity(UserType, ("FirstName", "f"));
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([EntityUpdateInfo<RtEntity>.CreateReplace(new RtEntityId(UserType, replace.RtId), replace)]));

        Assert.Contains(ex.Refusals, r =>
            r.AttributePath == "TwoFactorEnabled" && r.Cause == RtAttributeWriteRefusalCause.ReplaceWouldClear);
        Assert.Contains(ex.Refusals, r =>
            r.AttributePath == "Claims" && r.Cause == RtAttributeWriteRefusalCause.ReplaceWouldClear);
    }

    [Fact]
    public async Task OtherUserAttributes_SameNamesOnOtherTypes_AndDeletes_AreNotAffected()
    {
        await Write([Update(UserType, ("FirstName", "f"), ("LastName", "l"))]);
        await Write([Update(PlainType, ("TwoFactorEnabled", "x"))]);
        await Write([EntityUpdateInfo<RtEntity>.CreateDelete(new RtEntityId(UserType, OctoObjectId.GenerateNewId()))]);
    }

    [Fact]
    public async Task IdentityOwnedAttribute_IsAllowed_InTheMethodRunOfTheUserType()
    {
        ReplacedCreateTestSupport.ConfigureRun(_context, Marker(UserMethod()));

        await Write([Update(UserType, ("TwoFactorEnabled", "true"), ("Claims", new List<object>()))]);
    }

    [Fact]
    public async Task IdentityOwnedAttribute_IsRefused_InTheMethodRunOfAnotherType_OrPipeline()
    {
        ReplacedCreateTestSupport.ConfigureRun(_context, Marker(PlainMethod()));
        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([Update(UserType, ("TwoFactorEnabled", "true"))]));

        ReplacedCreateTestSupport.ConfigureRun(_context,
            Marker(UserMethod()) with { PipelineRtId = OctoObjectId.GenerateNewId().ToString() });
        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([Update(UserType, ("TwoFactorEnabled", "true"))]));
    }

    // -------------------------------------------------------------------------------------------- associations

    [Theory]
    [MemberData(nameof(IdentityRoles))]
    public async Task AssociationInsert_OfAnIdentityRole_IsRefused(string roleId)
    {
        var origin = new RtEntityId(UserType, OctoObjectId.GenerateNewId());
        var target = new RtEntityId(RoleType, OctoObjectId.GenerateNewId());

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([], AssociationUpdateInfo.CreateInsert(origin, target, new RtCkId<CkAssociationRoleId>(roleId))));

        var refusal = Assert.Single(ex.Refusals);
        Assert.Equal(RtAttributeWriteRefusalCause.IdentityOwned, refusal.Cause);
        Assert.Equal(RtAttributeWriteRefusalCause.AssociationAccess, refusal.Access);
        Assert.Contains(roleId, refusal.AttributePath);
        Assert.DoesNotContain(origin.RtId.ToString(), ex.Message);
        Assert.DoesNotContain(target.RtId.ToString(), ex.Message);
    }

    [Theory]
    [MemberData(nameof(IdentityRoles))]
    public async Task AssociationDelete_OfAnIdentityRole_IsRefused_InBothDirections(string roleId)
    {
        var role = new RtCkId<CkAssociationRoleId>(roleId);
        var user = new RtEntityId(UserType, OctoObjectId.GenerateNewId());
        var other = new RtEntityId(PlainType, OctoObjectId.GenerateNewId());

        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([], AssociationUpdateInfo.CreateDelete(user, other, role), AssociationUpdateInfo.CreateDelete(other, user, role)));

        Assert.Equal(2, ex.Refusals.Count);
    }

    [Fact]
    public async Task VersionQualifiedRoleIds_AreMatched()
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write([],
            AssociationUpdateInfo.CreateInsert(new RtEntityId(UserType, OctoObjectId.GenerateNewId()),
                new RtEntityId(RoleType, OctoObjectId.GenerateNewId()),
                new RtCkId<CkAssociationRoleId>("System.Identity-2.30.0/AssignedRole"))));

        Assert.Single(ex.Refusals);
    }

    [Fact]
    public async Task OtherAssociationRoles_AreNotAffected()
    {
        await Write([],
            AssociationUpdateInfo.CreateInsert(new RtEntityId(PlainType, OctoObjectId.GenerateNewId()),
                new RtEntityId(PlainType, OctoObjectId.GenerateNewId()), new RtCkId<CkAssociationRoleId>("System/ParentChild")),
            AssociationUpdateInfo.CreateInsert(new RtEntityId(UserType, OctoObjectId.GenerateNewId()),
                new RtEntityId(PlainType, OctoObjectId.GenerateNewId()),
                new RtCkId<CkAssociationRoleId>("System.Identity/IdentifiesUser")));
    }

    [Fact]
    public async Task IdentityAssociation_IsAllowed_InTheMethodRunOfASystemIdentityTypeAtEitherEnd()
    {
        ReplacedCreateTestSupport.ConfigureRun(_context, Marker(UserMethod()));
        var role = new RtCkId<CkAssociationRoleId>("System.Identity/AssignedRole");

        await Write([],
            AssociationUpdateInfo.CreateInsert(new RtEntityId(UserType, OctoObjectId.GenerateNewId()),
                new RtEntityId(RoleType, OctoObjectId.GenerateNewId()), role),
            AssociationUpdateInfo.CreateDelete(new RtEntityId(RoleType, OctoObjectId.GenerateNewId()),
                new RtEntityId(UserType, OctoObjectId.GenerateNewId()), role));
    }

    [Fact]
    public async Task IdentityAssociation_IsRefused_InTheMethodRunOfANonIdentityType()
    {
        // A method of a tenant model's type does not open the identity associations, even on its own entity.
        ReplacedCreateTestSupport.ConfigureRun(_context, Marker(PlainMethod()));

        await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() => Write([],
            AssociationUpdateInfo.CreateInsert(new RtEntityId(PlainType, OctoObjectId.GenerateNewId()),
                new RtEntityId(RoleType, OctoObjectId.GenerateNewId()),
                new RtCkId<CkAssociationRoleId>("System.Identity/AssignedRole"))));
    }

    [Fact]
    public async Task EntityAndAssociationRefusals_AreListedTogether_TheWholeBatchIsRefused()
    {
        var ex = await Assert.ThrowsAsync<RtAttributeWriteRestrictedException>(() =>
            Write([Update(UserType, ("EmailConfirmed", "true"))],
                AssociationUpdateInfo.CreateInsert(new RtEntityId(UserType, OctoObjectId.GenerateNewId()),
                    new RtEntityId(RoleType, OctoObjectId.GenerateNewId()),
                    new RtCkId<CkAssociationRoleId>("System.Identity/GroupMember"))));

        Assert.Equal(2, ex.Refusals.Count);
        Assert.Contains("attributes or associations", ex.Message);
    }

    // ------------------------------------------------------------------------------------------------- helpers

    private Task Write(IEntityUpdateInfo<RtEntity>[] updates, params AssociationUpdateInfo[] associations) =>
        RtAttributeAccessGuard.EnsureWritesAllowedAsync(_context, _cache, updates, associations);

    private static EntityUpdateInfo<RtEntity> Update(RtCkId<CkTypeId> type, params (string Name, object? Value)[] attributes)
    {
        var entity = Entity(type, attributes);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(type, entity.RtId), entity);
    }

    private static RtEntity Entity(RtCkId<CkTypeId> type, params (string Name, object? Value)[] attributes)
    {
        var entity = new RtEntity(type, OctoObjectId.GenerateNewId());
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }

        return entity;
    }

    private static CkMethodGraph UserMethod() => new(new CkId<CkTypeId>($"{IdentityModel}/User"),
        new CkMethodDto { MethodId = "SetRoles-1", Kind = CkMethodKindDto.Instance });

    private static CkMethodGraph PlainMethod() => new(new CkId<CkTypeId>("Test-1.0.0/Plain"),
        new CkMethodDto { MethodId = "DoIt-1", Kind = CkMethodKindDto.Instance });

    private static ExecutePipelineMethodInvocation Marker(CkMethodGraph method) => new()
    {
        MethodId = method.QualifiedMethodId,
        InvocationId = Guid.NewGuid(),
        CkTypeId = method.DeclaringCkTypeId.ToRtCkId().FullName,
        PipelineRtId = ReplacedCreateTestSupport.PipelineRtId.ToString()
    };

    private static CkTypeAttributeGraph Attr(string model, string name, CkAttributeAccessDto? access = null,
        AttributeValueTypesDto valueType = AttributeValueTypesDto.String, CkId<CkRecordId>? record = null)
    {
        var attrId = new CkId<CkAttributeId>($"{model}/{name}");
        var definition = new CkAttributeGraph(attrId,
            new CkAttributeDto { AttributeId = name, ValueType = valueType, ValueCkRecordId = record });
        return new CkTypeAttributeGraph(attrId,
            new CkTypeAttributeDto { CkAttributeId = attrId, AttributeName = name, IsOptional = true, Access = access },
            definition);
    }

    private static CkTypeGraph Type(string model, string name, CkMethodGraph? method, params CkTypeAttributeGraph[] attrs) =>
        new(new CkId<CkTypeId>($"{model}/{name}"), isAbstract: false, isFinal: true, isCollectionRoot: false,
            baseTypes: [], derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [],
            definedAttributes: [], allAttributes: attrs.ToDictionary(a => a.CkAttributeId, a => a), indexes: [],
            associations: new CkGraphDirectedAssociations([]), description: "test",
            enableChangeStreamPreAndPostImages: false,
            allMethods: method == null ? null : new Dictionary<string, CkMethodGraph> { [method.Definition.MethodId] = method });

    private static ICkCacheService Cache()
    {
        var claimRecordId = new CkId<CkRecordId>($"{IdentityModel}/UserClaim");
        var userAttributes = new List<CkTypeAttributeGraph>
        {
            Attr(IdentityModel, "FirstName"), Attr(IdentityModel, "LastName"),
            Attr(IdentityModel, "LockoutEnd", CkAttributeAccessDto.ReadOnly),
            Attr(IdentityModel, "AccessFailedCount", CkAttributeAccessDto.ReadOnly)
        };
        userAttributes.AddRange(OwnedReadWrite.Select(n => n == "Claims"
            ? Attr(IdentityModel, n, null, AttributeValueTypesDto.RecordArray, claimRecordId)
            : Attr(IdentityModel, n)));
        var user = Type(IdentityModel, "User", UserMethod(), userAttributes.ToArray());
        var role = Type(IdentityModel, "Role", null, Attr(IdentityModel, "Name"));
        var plain = Type("Test-1.0.0", "Plain", PlainMethod(), Attr("Test-1.0.0", "Name"),
            Attr("Test-1.0.0", "TwoFactorEnabled"));
        var claimAttributes = new[] { Attr(IdentityModel, "Type"), Attr(IdentityModel, "Value") };
        var claim = new CkRecordGraph(claimRecordId, isAbstract: false, isFinal: false, baseRecords: [],
            derivedFromCkRecordId: null, derivedRecords: [], definedAttributes: [],
            allAttributes: claimAttributes.ToDictionary(a => a.CkAttributeId, a => a), description: "test");

        var cache = A.Fake<ICkCacheService>();
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        foreach (var (id, graph) in new[] { (UserType, user), (RoleType, role), (PlainType, plain) })
        {
            var graphOut = (CkTypeGraph?)graph;
            A.CallTo(() => cache.TryGetRtCkType(Tenant, id, out graphOut)).Returns(true).AssignsOutAndRefParameters(graph);
            A.CallTo(() => cache.GetRtCkType(Tenant, id)).Returns(graph);
        }

        A.CallTo(() => cache.GetCkTypes(Tenant)).Returns(new List<CkTypeGraph> { user, role, plain });
        var claimOut = (CkRecordGraph?)claim;
        A.CallTo(() => cache.TryGetRtCkRecord(Tenant, A<RtCkId<CkRecordId>>._, out claimOut)).Returns(true)
            .AssignsOutAndRefParameters(claim);
        A.CallTo(() => cache.GetRtCkRecord(Tenant, A<RtCkId<CkRecordId>>._)).Returns(claim);
        return cache;
    }
}
