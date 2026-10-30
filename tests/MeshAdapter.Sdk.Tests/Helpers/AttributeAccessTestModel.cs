using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.MeshAdapter;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
///     AB#7768 — a synthetic CK graph with every attribute access, modelled on System.Identity/User:
///     type <c>Test/Account</c> has Name and NormalizedEmail (ReadWrite), Created (ReadOnly), Email (MethodOnly),
///     PasswordHash (Hidden), Profile (record) and Credentials (record array); record <c>Test/Credential</c> has
///     Label (ReadWrite) and Token (Hidden). The instance method <c>SetEmail-1</c> belongs to Account.
///     <c>Test/Plain</c> has only Name.
/// </summary>
internal static class AttributeAccessTestModel
{
    public const string Tenant = ReplacedCreateTestSupport.Tenant;
    public const string Model = "Test-1.0.0";

    public static readonly RtCkId<CkTypeId> AccountType = new("Test/Account");
    public static readonly RtCkId<CkTypeId> PlainType = new("Test/Plain");
    public static readonly RtCkId<CkRecordId> CredentialRecord = new("Test/Credential");
    private static readonly CkId<CkRecordId> CredentialRecordId = new("Test-1.0.0/Credential");

    public static string MethodId { get; } = Method().QualifiedMethodId;

    private static CkMethodGraph Method() => new(new CkId<CkTypeId>($"{Model}/Account"),
        new CkMethodDto { MethodId = "SetEmail-1", Kind = CkMethodKindDto.Instance });

    private static CkTypeAttributeGraph Attr(string name, CkAttributeAccessDto? access,
        AttributeValueTypesDto valueType = AttributeValueTypesDto.String, CkId<CkRecordId>? record = null)
    {
        var attrId = new CkId<CkAttributeId>($"{Model}/{name}");
        var definition = new CkAttributeGraph(attrId,
            new CkAttributeDto { AttributeId = name, ValueType = valueType, ValueCkRecordId = record });
        return new CkTypeAttributeGraph(attrId,
            new CkTypeAttributeDto { CkAttributeId = attrId, AttributeName = name, IsOptional = true, Access = access },
            definition);
    }

    private static CkTypeGraph Type(string name, bool withMethod, params CkTypeAttributeGraph[] attrs) => new(
        new CkId<CkTypeId>($"{Model}/{name}"), isAbstract: false, isFinal: false, isCollectionRoot: false,
        baseTypes: [], derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [],
        definedAttributes: [], allAttributes: attrs.ToDictionary(a => a.CkAttributeId, a => a), indexes: [],
        associations: new CkGraphDirectedAssociations([]), description: "test",
        enableChangeStreamPreAndPostImages: false,
        allMethods: withMethod ? new Dictionary<string, CkMethodGraph> { ["SetEmail-1"] = Method() } : null);

    public static CkTypeGraph AccountGraph() => Type("Account", true,
        Attr("Name", null),
        Attr("NormalizedEmail", null),
        Attr("Created", CkAttributeAccessDto.ReadOnly),
        Attr("Email", CkAttributeAccessDto.MethodOnly),
        Attr("PasswordHash", CkAttributeAccessDto.Hidden),
        Attr("Profile", null, AttributeValueTypesDto.Record, CredentialRecordId),
        Attr("Credentials", null, AttributeValueTypesDto.RecordArray, CredentialRecordId));

    public static CkRecordGraph CredentialGraph()
    {
        var attrs = new[] { Attr("Label", null), Attr("Token", CkAttributeAccessDto.Hidden) };
        return new CkRecordGraph(CredentialRecordId, isAbstract: false, isFinal: false, baseRecords: [],
            derivedFromCkRecordId: null, derivedRecords: [], definedAttributes: [],
            allAttributes: attrs.ToDictionary(a => a.CkAttributeId, a => a), description: "test");
    }

    /// <summary>A loaded CK cache that knows Account, Plain and the Credential record.</summary>
    public static ICkCacheService Cache()
    {
        var cache = A.Fake<ICkCacheService>();
        var account = AccountGraph();
        var plain = Type("Plain", false, Attr("Name", null));
        var record = CredentialGraph();
        var accountOut = (CkTypeGraph?)account;
        var plainOut = (CkTypeGraph?)plain;
        var recordOut = (CkRecordGraph?)record;
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        A.CallTo(() => cache.GetRtCkType(Tenant, AccountType))
            .Returns(account);
        A.CallTo(() => cache.GetRtCkType(Tenant, PlainType))
            .Returns(plain);
        A.CallTo(() => cache.TryGetRtCkType(Tenant, AccountType,
            out accountOut)).Returns(true).AssignsOutAndRefParameters(account);
        A.CallTo(() => cache.TryGetRtCkType(Tenant, PlainType,
            out plainOut)).Returns(true).AssignsOutAndRefParameters(plain);
        A.CallTo(() => cache.GetCkTypes(Tenant)).Returns(new List<CkTypeGraph> { account, plain });
        A.CallTo(() => cache.GetRtCkRecord(Tenant, A<RtCkId<CkRecordId>>._)).Returns(record);
        A.CallTo(() => cache.TryGetRtCkRecord(Tenant, A<RtCkId<CkRecordId>>._, out recordOut)).Returns(true)
            .AssignsOutAndRefParameters(record);
        return cache;
    }

    public static RtEntity Account(params (string Name, object? Value)[] attributes) =>
        Entity(AccountType, attributes);

    public static RtEntity Entity(RtCkId<CkTypeId> type, params (string Name, object? Value)[] attributes)
    {
        var entity = new RtEntity(type, OctoObjectId.GenerateNewId())
        {
            RtWellKnownName = "wkn",
            RtVersion = 7,
            RtCreatedBy = "creator"
        };
        foreach (var (name, value) in attributes)
        {
            entity.SetAttributeRawValue(name, value);
        }

        return entity;
    }

    public static RtRecord Credential(params (string Name, object? Value)[] members) =>
        new(CredentialRecord, members.ToDictionary(m => m.Name, m => m.Value));

    public static EntityUpdateInfo<RtEntity> Insert(params (string Name, object? Value)[] attributes) =>
        EntityUpdateInfo<RtEntity>.CreateInsert(AccountType, Account(attributes));

    public static EntityUpdateInfo<RtEntity> Update(params (string Name, object? Value)[] attributes)
    {
        var entity = Account(attributes);
        return EntityUpdateInfo<RtEntity>.CreateUpdate(new RtEntityId(AccountType, entity.RtId), entity);
    }

    public static EntityUpdateInfo<RtEntity> Replace(params (string Name, object? Value)[] attributes)
    {
        var entity = Account(attributes);
        return EntityUpdateInfo<RtEntity>.CreateReplace(new RtEntityId(AccountType, entity.RtId), entity);
    }

    /// <summary>The marker the controller sends for a run of <c>SetEmail-1</c> on Account by the running pipeline.</summary>
    public static ExecutePipelineMethodInvocation MethodRunMarker() => new()
    {
        MethodId = MethodId,
        InvocationId = Guid.NewGuid(),
        CkTypeId = "Test/Account",
        PipelineRtId = ReplacedCreateTestSupport.PipelineRtId.ToString()
    };

    public static void ConfigureRun(IMeshEtlContext context, ExecutePipelineMethodInvocation? marker) =>
        ReplacedCreateTestSupport.ConfigureRun(context, marker);
}
