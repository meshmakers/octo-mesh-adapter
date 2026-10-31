using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.Formulas;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

namespace MeshAdapter.Sdk.Tests.Nodes.Transform;

/// <summary>
/// Pins the CK-value-type → <see cref="FormulaResultType"/> mapping the node uses to cast a mapping
/// expression's numeric result back to the target attribute's CLR type. This is what lets a
/// DataPointMapping drive a Boolean target (e.g. <c>IsCharging ← value &gt; 0</c>) — the
/// AttributeValueConverter does not coerce a raw double into a bool, so the node must produce a
/// typed value via <c>IFormulaEngine.Evaluate(..., FormulaResultType)</c>.
/// Also pins that the node emits ONE update per target entity (AB#7962): several mappings of one source
/// state onto the same target must not produce several updates, because SaveStreamDataInArchive@1 writes
/// one archive row per update item.
/// </summary>
public class ApplyDataPointMappingsNodeTests : SessionNodeTestBase
{
    private const string Tenant = "test-tenant";
    private const string Model = "Test-1.0.0";
    private const string StateName = "activeMoodsNum";

    private static readonly RtCkId<CkTypeId> SourceType = new("Loxone/Control");
    private static readonly RtCkId<CkTypeId> LuminaireType = new("Test/Luminaire");
    private static readonly RtCkId<CkTypeId> DataPointMappingType = new("System.Communication/DataPointMapping");
    private static readonly RtCkId<CkAssociationRoleId> MapsFromRole = new("System.Communication/MapsFrom");
    private static readonly RtCkId<CkAssociationRoleId> MapsToRole = new("System.Communication/MapsTo");

    private static readonly OctoObjectId SourceRtId = new("0000000000000000000000a1");
    private static readonly OctoObjectId LuminaireA = new("0000000000000000000000b1");
    private static readonly OctoObjectId LuminaireB = new("0000000000000000000000b2");

    private readonly ICkCacheService _cache;
    private readonly IFormulaEngine _formulaEngine = A.Fake<IFormulaEngine>();
    private readonly List<(OctoObjectId MappingRtId, string TargetAttribute, OctoObjectId TargetRtId, string? Expression)>
        _mappings = [];

    public ApplyDataPointMappingsNodeTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(Tenant);
        _cache = LuminaireCache();
        // "value > 0" for the Boolean IsOn target evaluates to true for any positive mood number.
        A.CallTo(() => _formulaEngine.Evaluate(A<string>._, A<IReadOnlyDictionary<string, double>>._,
                FormulaResultType.Boolean))
            .ReturnsLazily((string _, IReadOnlyDictionary<string, double> args, FormulaResultType _) =>
                args["value"] > 0);
    }

    [Fact]
    public async Task TwoMappingsOfOneStateToTheSameTarget_ProduceOneUpdateWithBothAttributes()
    {
        GivenMapping("0000000000000000000000c1", "IsOn", LuminaireA, "value > 0");
        GivenMapping("0000000000000000000000c2", "DimmingLevel", LuminaireA, null);

        var updates = await RunAsync(sourceValue: 3.0);

        var update = Assert.Single(updates);
        Assert.Equal(new RtEntityId(LuminaireType, LuminaireA), update.GetRtEntityId());
        Assert.Equal(true, update.RtEntity!.GetAttributeValueOrDefault("IsOn"));
        Assert.Equal(3.0, update.RtEntity.GetAttributeValueOrDefault("DimmingLevel"));
    }

    [Fact]
    public async Task MappingsOfOneStateToTwoTargets_ProduceOneUpdatePerTarget()
    {
        GivenMapping("0000000000000000000000c1", "DimmingLevel", LuminaireA, null);
        GivenMapping("0000000000000000000000c2", "DimmingLevel", LuminaireB, null);

        var updates = await RunAsync(sourceValue: 42.0);

        Assert.Equal(2, updates.Count);
        Assert.Equal(new RtEntityId(LuminaireType, LuminaireA), updates[0].GetRtEntityId());
        Assert.Equal(new RtEntityId(LuminaireType, LuminaireB), updates[1].GetRtEntityId());
        Assert.All(updates, u => Assert.Equal(42.0, u.RtEntity!.GetAttributeValueOrDefault("DimmingLevel")));
    }

    [Fact]
    public async Task AllUpdatesOfOneInvocation_ShareOneRtChangedDateTime()
    {
        GivenMapping("0000000000000000000000c1", "IsOn", LuminaireA, "value > 0");
        GivenMapping("0000000000000000000000c2", "DimmingLevel", LuminaireA, null);
        GivenMapping("0000000000000000000000c3", "DimmingLevel", LuminaireB, null);

        var before = DateTime.UtcNow;
        var updates = await RunAsync(sourceValue: 1.0);
        var after = DateTime.UtcNow;

        Assert.Equal(2, updates.Count);
        var timestamps = updates.Select(u => u.RtEntity!.RtChangedDateTime).Distinct().ToList();
        var timestamp = Assert.Single(timestamps);
        Assert.InRange(timestamp!.Value, before, after);
    }

    private void GivenMapping(string mappingRtId, string targetAttribute, OctoObjectId targetRtId, string? expression)
    {
        _mappings.Add((new OctoObjectId(mappingRtId), targetAttribute, targetRtId, expression));
    }

    private async Task<List<EntityUpdateInfo<RtEntity>>> RunAsync(double sourceValue)
    {
        // Source ◀ MapsFrom ◀ DataPointMapping (inbound from the source's view).
        var inbound = _mappings.Select(m => new RtAssociation
        {
            OriginRtId = m.MappingRtId,
            OriginCkTypeId = DataPointMappingType,
            TargetRtId = SourceRtId,
            TargetCkTypeId = SourceType,
            AssociationRoleId = MapsFromRole
        }).ToList();
        A.CallTo(() => TenantRepository.GetRtAssociationsAsync(A<IOctoSession>._,
                new RtEntityId(SourceType, SourceRtId), A<RtAssociationExtendedQueryOptions>._))
            .Returns(ResultSet(inbound));

        foreach (var m in _mappings)
        {
            var mapping = new RtEntity(DataPointMappingType, m.MappingRtId);
            mapping.SetAttributeRawValue("SourceAttributePath", StateName);
            mapping.SetAttributeRawValue("TargetAttributePath", m.TargetAttribute);
            mapping.SetAttributeRawValue("MappingExpression", m.Expression);
            var mappingRtId = m.MappingRtId;
            A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(A<IOctoSession>._, DataPointMappingType,
                    A<IReadOnlyList<OctoObjectId>>.That.Matches(ids => ids.Contains(mappingRtId)),
                    A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
                .Returns(ResultSet(new List<RtEntity> { mapping }));

            var mapsTo = new RtAssociation
            {
                OriginRtId = m.MappingRtId,
                OriginCkTypeId = DataPointMappingType,
                TargetRtId = m.TargetRtId,
                TargetCkTypeId = LuminaireType,
                AssociationRoleId = MapsToRole
            };
            A.CallTo(() => TenantRepository.GetRtAssociationsAsync(A<IOctoSession>._,
                    new RtEntityId(DataPointMappingType, mappingRtId), A<RtAssociationExtendedQueryOptions>._))
                .Returns(ResultSet(new List<RtAssociation> { mapsTo }));
        }

        var config = new ApplyDataPointMappingsNodeConfiguration
        {
            SourceRtIdPath = "$.rtId",
            SourceCkTypeIdPath = "$.ckTypeId",
            SourceValuePath = "$.value",
            SourceStateNamePath = "$.state",
            TargetPath = "$.updates"
        };
        var (dataContext, nodeContext, next) = PrepareTest(config);
        SetupGetSimpleValueByPath<OctoObjectId?>(dataContext, "$.rtId", SourceRtId);
        SetupGetSimpleValueByPath<RtCkId<CkTypeId>?>(dataContext, "$.ckTypeId", SourceType);
        SetupGetSimpleValueByPath(dataContext, "$.state", StateName);
        A.CallTo(() => dataContext.GetValue("$.value", A<bool>._)).Returns(sourceValue);

        List<EntityUpdateInfo<RtEntity>>? captured = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                           && call.Arguments.Count >= 2
                           && (call.Arguments[0] as string) == config.TargetPath
                           && call.Arguments[1] is List<EntityUpdateInfo<RtEntity>>)
            .Invokes(call => captured = (List<EntityUpdateInfo<RtEntity>>)call.Arguments[1]!);

        var node = new ApplyDataPointMappingsNode(next, EtlContext, _cache, _formulaEngine);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
        Assert.NotNull(captured);
        return captured!;
    }

    private static IResultSet<T> ResultSet<T>(List<T> items)
    {
        var set = A.Fake<IResultSet<T>>();
        A.CallTo(() => set.Items).Returns(items);
        A.CallTo(() => set.TotalCount).Returns(items.Count);
        return set;
    }

    private static ICkCacheService LuminaireCache()
    {
        static CkTypeAttributeGraph Attr(string name, AttributeValueTypesDto valueType)
        {
            var attrId = new CkId<CkAttributeId>($"{Model}/{name}");
            var definition = new CkAttributeGraph(attrId,
                new CkAttributeDto { AttributeId = name, ValueType = valueType });
            return new CkTypeAttributeGraph(attrId,
                new CkTypeAttributeDto { CkAttributeId = attrId, AttributeName = name, IsOptional = true },
                definition);
        }

        var attrs = new[] { Attr("IsOn", AttributeValueTypesDto.Boolean), Attr("DimmingLevel", AttributeValueTypesDto.Double) };
        var luminaire = new CkTypeGraph(
            new CkId<CkTypeId>($"{Model}/Luminaire"), isAbstract: false, isFinal: false, isCollectionRoot: false,
            baseTypes: [], derivedFromCkTypeId: null, definingCollectionRootCkTypeId: null, derivedTypes: [],
            definedAttributes: [], allAttributes: attrs.ToDictionary(a => a.CkAttributeId, a => a), indexes: [],
            associations: new CkGraphDirectedAssociations([]), description: "test",
            enableChangeStreamPreAndPostImages: false);

        var cache = A.Fake<ICkCacheService>();
        var luminaireOut = (CkTypeGraph?)luminaire;
        A.CallTo(() => cache.IsTenantLoaded(Tenant)).Returns(true);
        A.CallTo(() => cache.GetRtCkType(Tenant, LuminaireType)).Returns(luminaire);
        A.CallTo(() => cache.TryGetRtCkType(Tenant, LuminaireType, out luminaireOut))
            .Returns(true).AssignsOutAndRefParameters(luminaire);
        A.CallTo(() => cache.GetCkTypes(Tenant)).Returns(new List<CkTypeGraph> { luminaire });
        return cache;
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.Boolean, FormulaResultType.Boolean)]
    [InlineData(AttributeValueTypesDto.Int, FormulaResultType.Int)] // == Integer
    [InlineData(AttributeValueTypesDto.Enum, FormulaResultType.Int)]
    [InlineData(AttributeValueTypesDto.Int64, FormulaResultType.Int64)] // == Integer64
    [InlineData(AttributeValueTypesDto.Double, FormulaResultType.Double)]
    [InlineData(AttributeValueTypesDto.DateTime, FormulaResultType.DateTime)]
    public void MapValueTypeToFormulaResultType_ScalarTypes_AreTyped(
        AttributeValueTypesDto valueType, FormulaResultType expected)
    {
        Assert.Equal(expected, ApplyDataPointMappingsNode.MapValueTypeToFormulaResultType(valueType));
    }

    [Theory]
    [InlineData(AttributeValueTypesDto.String)]
    [InlineData(AttributeValueTypesDto.Binary)]
    [InlineData(AttributeValueTypesDto.BinaryLinked)]
    [InlineData(AttributeValueTypesDto.StringArray)]
    [InlineData(AttributeValueTypesDto.IntArray)]
    [InlineData(AttributeValueTypesDto.Record)]
    [InlineData(AttributeValueTypesDto.RecordArray)]
    [InlineData(AttributeValueTypesDto.TimeSpan)]
    [InlineData(AttributeValueTypesDto.DateTimeOffset)]
    [InlineData(AttributeValueTypesDto.GeospatialPoint)]
    public void MapValueTypeToFormulaResultType_NonScalarTypes_FallBackToRawDouble(
        AttributeValueTypesDto valueType)
    {
        // null signals the caller to keep the raw EvaluateRaw(double) path.
        Assert.Null(ApplyDataPointMappingsNode.MapValueTypeToFormulaResultType(valueType));
    }
}
