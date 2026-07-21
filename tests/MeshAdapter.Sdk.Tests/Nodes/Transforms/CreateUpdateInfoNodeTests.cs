using System.Collections;
using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class CreateUpdateInfoNodeTests
{
    private const string TestTenantId = "test-tenant";
    private static readonly RtCkId<CkTypeId> TestRtCkTypeId = new("TestModel/TestType");
    private static readonly OctoObjectId TestRtId = new("000000000000000000000001");

    private static (IDataContext, INodeContext, IMeshEtlContext, ICkCacheService, NodeDelegate) PrepareTest(
        CreateUpdateInfoNodeConfiguration config,
        JsonNode testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var meshEtlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => meshEtlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => meshEtlContext.TenantId).Returns(TestTenantId);

        // Use a real DataContextImpl so EnumerateMatches works against the test data.
        IDataContext dataContext = new DataContextImpl(JsonDocument.Parse(testData.ToJsonString()));

        var rootNodeContext =
            NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("CreateUpdateInfo", 0, config, dataContext);

        var ckCacheService = A.Fake<ICkCacheService>();
        var next = A.Fake<NodeDelegate>();

        return (dataContext, nodeContext, meshEtlContext, ckCacheService, next);
    }

    /// <summary>
    /// Configures the cache to return a CkTypeGraph that exposes a single attribute of the
    /// given name and value type. Sufficient for <see cref="RtPathEvaluator.SetValue"/> to
    /// resolve a top-level attribute path.
    /// </summary>
    private static void SetupCacheForAttribute(ICkCacheService ckCacheService, string attributeName,
        AttributeValueTypesDto valueType)
    {
        var attrId = new CkId<CkAttributeId>("TestModel", new CkAttributeId(attributeName));
        var attributeGraph = new CkTypeAttributeGraph(
            ckAttributeId: attrId,
            attributeName: attributeName,
            autoCompleteValues: null,
            valueType: valueType,
            valueCkRecordId: null,
            valueCkEnumId: null,
            autoIncrementReference: null,
            metaData: null,
            defaultValues: null,
            isOptional: true,
            description: null);

        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
        {
            [attrId] = attributeGraph
        };

        var ckTypeGraph = new CkTypeGraph(
            ckTypeId: new CkId<CkTypeId>("TestModel", new CkTypeId("TestType")),
            isAbstract: false,
            isFinal: false,
            isCollectionRoot: false,
            baseTypes: [],
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: [],
            definedAttributes: [],
            allAttributes: allAttributes,
            indexes: [],
            associations: new CkGraphDirectedAssociations([]),
            description: string.Empty,
            enableChangeStreamPreAndPostImages: false);

        A.CallTo(() => ckCacheService.TryGetRtCkType(TestTenantId, A<RtCkId<CkTypeId>>._, out ckTypeGraph!))
            .Returns(true)
            .AssignsOutAndRefParameters(ckTypeGraph);
    }

    [Fact]
    public async Task ProcessObjectAsync_MultiMatchValuePath_ProducesUpdatePerMatch()
    {
        // Arrange — wildcard ValuePath "$.vals[*]" matches three integer values.
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = TestRtId,
            CkTypeId = TestRtCkTypeId,
            TargetPath = "$.result",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new()
                {
                    AttributeName = "v",
                    AttributeValueType = AttributeValueTypesDto.Int,
                    ValuePath = "$.vals[*]"
                }
            }
        };

        var testData = new JsonObject
        {
            ["vals"] = new JsonArray(10, 20, 30)
        };

        var (dataContextReal, nodeContext, meshEtlContext, ckCacheService, next) =
            PrepareTest(config, testData);

        SetupCacheForAttribute(ckCacheService, "V", AttributeValueTypesDto.Int);

        // Wrap the real data context so we can both observe Set calls and forward
        // EnumerateMatches/Get to the real implementation.
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(dataContextReal));

        EntityUpdateInfo<RtEntity>? capturedUpdate = null;
        A.CallTo(() => dataContext.Set(
                "$.result",
                A<EntityUpdateInfo<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity>? u, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedUpdate = u);

        var node = new CreateUpdateInfoNode(next, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert — multi-match path must be honored via SelectMatches and produce an
        // update for every match (not silently drop all but one).
        A.CallTo(() => dataContext.SelectMatches("$.vals[*]")).MustHaveHappened();

        Assert.NotNull(capturedUpdate);
        Assert.NotNull(capturedUpdate!.RtEntity);

        // Each match calls SetAttributeValueSingle → RtPathEvaluator.SetValue, which
        // overwrites the attribute on the RtEntity. Sequential overwrites mirror legacy
        // SelectTokens behavior; the last match wins.
        Assert.True(capturedUpdate.RtEntity!.Attributes.ContainsKey("V"));
        Assert.Equal(30, capturedUpdate.RtEntity.Attributes["V"]);

        // Cache must have been hit once per match (3 matches → 3 SetValue invocations).
        var ignored = (CkTypeGraph?)null;
        A.CallTo(() => ckCacheService.TryGetRtCkType(TestTenantId, A<RtCkId<CkTypeId>>._, out ignored!))
            .MustHaveHappened(3, Times.Exactly);
    }

    [Fact]
    public async Task ProcessObjectAsync_TimestampPathMissing_FallsBackToUtcNow()
    {
        // When TimestampPath is configured but the path is absent, the change timestamp must fall
        // back to DateTime.UtcNow — not DateTime.MinValue (which Get<DateTime> would yield for a
        // missing path). Pins the Newtonsoft-era live-timestamp behavior.
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = TestRtId,
            CkTypeId = TestRtCkTypeId,
            TargetPath = "$.result",
            TimestampPath = "$.missingTimestamp",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new()
                {
                    AttributeName = "v",
                    AttributeValueType = AttributeValueTypesDto.Int,
                    ValuePath = "$.vals[*]"
                }
            }
        };

        // No $.missingTimestamp in the document.
        var testData = new JsonObject
        {
            ["vals"] = new JsonArray(10)
        };

        var (dataContextReal, nodeContext, meshEtlContext, ckCacheService, next) =
            PrepareTest(config, testData);

        SetupCacheForAttribute(ckCacheService, "V", AttributeValueTypesDto.Int);

        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(dataContextReal));

        EntityUpdateInfo<RtEntity>? capturedUpdate = null;
        A.CallTo(() => dataContext.Set(
                "$.result",
                A<EntityUpdateInfo<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity>? u, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedUpdate = u);

        var node = new CreateUpdateInfoNode(next, meshEtlContext, ckCacheService);

        var before = DateTime.UtcNow;
        await node.ProcessObjectAsync(dataContext, nodeContext);
        var after = DateTime.UtcNow;

        Assert.NotNull(capturedUpdate);
        Assert.NotNull(capturedUpdate!.RtEntity);
        var ts = capturedUpdate.RtEntity!.RtChangedDateTime;
        Assert.NotNull(ts);
        // Must be a live timestamp, not the epoch minimum.
        Assert.NotEqual(default, ts!.Value);
        Assert.InRange(ts.Value, before.AddSeconds(-5), after.AddSeconds(5));
    }

    [Fact]
    public async Task ProcessObjectAsync_StringAttributeFromObject_SerializesNonAsciiLiterally()
    {
        // When a String target attribute receives an object/array, it is stored as compact JSON.
        // For Newtonsoft (ToString(Formatting.None)) parity, non-ASCII must be emitted LITERALLY
        // ("Müller"), not escaped ("Müller") — otherwise the stored value diverges from
        // pre-migration data and breaks downstream string equality (e.g. CheckDuplicate's
        // FieldFilter Equals against an existing attribute value).
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = TestRtId,
            CkTypeId = TestRtCkTypeId,
            TargetPath = "$.result",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new()
                {
                    AttributeName = "payload",
                    AttributeValueType = AttributeValueTypesDto.String,
                    ValuePath = "$.obj"
                }
            }
        };

        var testData = new JsonObject
        {
            ["obj"] = new JsonObject { ["name"] = "Müller" }
        };

        var (dataContextReal, nodeContext, meshEtlContext, ckCacheService, next) =
            PrepareTest(config, testData);

        SetupCacheForAttribute(ckCacheService, "Payload", AttributeValueTypesDto.String);

        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(dataContextReal));

        EntityUpdateInfo<RtEntity>? capturedUpdate = null;
        A.CallTo(() => dataContext.Set(
                "$.result",
                A<EntityUpdateInfo<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity>? u, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedUpdate = u);

        var node = new CreateUpdateInfoNode(next, meshEtlContext, ckCacheService);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdate);
        Assert.NotNull(capturedUpdate!.RtEntity);
        Assert.Equal("{\"name\":\"Müller\"}", capturedUpdate.RtEntity!.Attributes["Payload"]);
    }

    /// <summary>
    /// Regression for AB#3517: after the STJ migration, <c>RtCkIdRecordIdConverter</c> serializes a
    /// record's <c>CkRecordId</c> as a <em>string</em> (its <c>SemanticVersionedFullName</c>) rather
    /// than the legacy reflection-emitted object shape. A record-array attribute fed array items of
    /// the string form crashed downstream with
    /// "Unable to cast object of type 'System.Text.Json.Nodes.JsonObject' to type 'RtRecord'":
    /// <see cref="CreateUpdateInfoNode.GetAttributeValue"/> only recognized the OBJECT form, so the
    /// raw <see cref="JsonObject"/> fell through into the stored <c>List&lt;object&gt;</c> instead of
    /// being reconstructed into an <see cref="RtRecord"/>. This test pins that each array item with a
    /// string-form CkRecordId is materialized as a real <see cref="RtRecord"/> (NOT a
    /// <see cref="JsonObject"/>) — failing pre-fix, passing post-fix.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_RecordArrayWithStringFormCkRecordId_MaterializesRtRecords()
    {
        // Arrange — a record-LIST attribute fed an array whose items carry the string-form CkRecordId
        // produced by RtCkIdRecordIdConverter post-STJ migration.
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = TestRtId,
            CkTypeId = TestRtCkTypeId,
            TargetPath = "$.result",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new()
                {
                    AttributeName = "items",
                    AttributeValueType = AttributeValueTypesDto.RecordArray,
                    ValuePath = "$.items"
                }
            }
        };

        // String-form CkRecordId ("TestModel/Item-1") — the canonical post-fix wire shape. Pre-fix the
        // node only matched a JsonObject-shaped CkRecordId, so this object never became an RtRecord.
        var testData = new JsonObject
        {
            ["items"] = new JsonArray(
                new JsonObject
                {
                    ["CkRecordId"] = "TestModel/Item-1",
                    ["Attributes"] = new JsonObject { ["Name"] = "abc" }
                })
        };

        var (dataContextReal, nodeContext, meshEtlContext, ckCacheService, next) =
            PrepareTest(config, testData);

        // Top-level "items" attribute is a RecordArray. RtPathEvaluator.SetValue replaces the whole
        // array (no index) by Cast<object?>().ToList(), so the items pass through untouched — meaning
        // whatever GetAttributeValue produced for each item is exactly what ends up stored.
        SetupCacheForAttribute(ckCacheService, "Items", AttributeValueTypesDto.RecordArray);

        // GetAttributeValue resolves the per-item CkRecordId via GetRtCkRecord(tenant, semanticName).
        // Return a CkRecordGraph whose AllAttributesByName contains a single "Name" String attribute so
        // the inner walker validates "Name" and runs recordChild.SetAttributeValue("Name", String, "abc").
        var itemRecordGraph = BuildSingleStringAttributeRecordGraph("TestModel/Item-1", "Name");
        A.CallTo(() => ckCacheService.GetRtCkRecord(TestTenantId, A<RtCkId<CkRecordId>>._))
            .Returns(itemRecordGraph);

        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(dataContextReal));

        EntityUpdateInfo<RtEntity>? capturedUpdate = null;
        A.CallTo(() => dataContext.Set(
                "$.result",
                A<EntityUpdateInfo<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity>? u, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedUpdate = u);

        var node = new CreateUpdateInfoNode(next, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert — the stored attribute must be a list whose item is a real RtRecord, not a JsonObject.
        Assert.NotNull(capturedUpdate);
        Assert.NotNull(capturedUpdate!.RtEntity);
        Assert.True(capturedUpdate.RtEntity!.Attributes.ContainsKey("Items"));

        var stored = capturedUpdate.RtEntity.Attributes["Items"];
        var storedList = Assert.IsAssignableFrom<IEnumerable>(stored).Cast<object?>().ToList();
        Assert.Single(storedList);

        var firstItem = storedList[0];
        // Core of the regression: pre-fix this is a System.Text.Json.Nodes.JsonObject; post-fix it is a
        // properly reconstructed RtRecord.
        Assert.IsType<RtRecord>(firstItem);
        var record = (RtRecord)firstItem!;

        // The inner "Name" attribute must have round-tripped onto the reconstructed record.
        Assert.True(record.Attributes.ContainsKey("Name"));
        Assert.Equal("abc", record.Attributes["Name"]);
    }

    /// <summary>
    /// Schema-inferred record coercion: a RecordArray attribute fed PLAIN JSON objects (no
    /// {CkRecordId, Attributes} envelope — the natural shape of LLM/webhook payloads) must
    /// materialize RtRecords using the record type DECLARED by the CK schema (valueCkRecordId),
    /// with property names matched case-insensitively (camelCase JSON → PascalCase CK attributes).
    /// Pre-enhancement these items fell through as JsonObjects and crashed downstream in
    /// AttributeValueConverter with "Unable to cast ... JsonObject to ... RtRecord".
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_RecordArrayFromPlainObjects_CoercesViaDeclaredRecordType()
    {
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = TestRtId,
            CkTypeId = TestRtCkTypeId,
            TargetPath = "$.result",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new()
                {
                    // camelCase on purpose: attribute resolution must be case-insensitive,
                    // mirroring RtPathEvaluator's semantics.
                    AttributeName = "sections",
                    AttributeValueType = AttributeValueTypesDto.RecordArray,
                    ValuePath = "$.sections"
                }
            }
        };

        // Plain objects, camelCase properties — no envelope anywhere.
        var testData = new JsonObject
        {
            ["sections"] = new JsonArray(
                new JsonObject { ["heading"] = "Summary", ["sortOrder"] = 0 },
                new JsonObject { ["heading"] = "Decisions", ["sortOrder"] = 1 })
        };

        var (dataContextReal, nodeContext, meshEtlContext, ckCacheService, next) =
            PrepareTest(config, testData);

        // Entity type declares "Sections" as RecordArray of TestModel/Section-1.
        var sectionRecordCkId = new CkId<CkRecordId>("TestModel/Section-1");
        SetupCacheForRecordArrayAttribute(ckCacheService, "Sections", sectionRecordCkId);

        var sectionRecordGraph = BuildRecordGraph("TestModel/Section-1",
            ("Heading", AttributeValueTypesDto.String),
            ("SortOrder", AttributeValueTypesDto.Int));
        A.CallTo(() => ckCacheService.GetRtCkRecord(TestTenantId, A<RtCkId<CkRecordId>>._))
            .Returns(sectionRecordGraph);

        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(dataContextReal));

        EntityUpdateInfo<RtEntity>? capturedUpdate = null;
        A.CallTo(() => dataContext.Set(
                "$.result",
                A<EntityUpdateInfo<RtEntity>?>._,
                A<DocumentModes>._,
                A<ValueKinds>._,
                A<TargetValueWriteModes>._))
            .Invokes((string _, EntityUpdateInfo<RtEntity>? u, DocumentModes _, ValueKinds _,
                TargetValueWriteModes _) => capturedUpdate = u);

        var node = new CreateUpdateInfoNode(next, meshEtlContext, ckCacheService);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedUpdate);
        Assert.NotNull(capturedUpdate!.RtEntity);
        Assert.True(capturedUpdate.RtEntity!.Attributes.ContainsKey("Sections"));

        var storedList = Assert.IsAssignableFrom<IEnumerable>(capturedUpdate.RtEntity.Attributes["Sections"])
            .Cast<object?>().ToList();
        Assert.Equal(2, storedList.Count);

        var first = Assert.IsType<RtRecord>(storedList[0]);
        // camelCase "heading" must land on the PascalCase CK attribute "Heading".
        Assert.Equal("Summary", first.Attributes["Heading"]);
        Assert.True(first.Attributes.ContainsKey("SortOrder"));

        var second = Assert.IsType<RtRecord>(storedList[1]);
        Assert.Equal("Decisions", second.Attributes["Heading"]);
    }

    /// <summary>
    /// Strictness parity with the envelope path: a plain-object property that matches no attribute
    /// of the declared record type must throw (loud failure on LLM typos / schema drift), not be
    /// silently dropped or stored raw.
    /// </summary>
    [Fact]
    public async Task ProcessObjectAsync_PlainObjectWithUnknownProperty_Throws()
    {
        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = UpdateKind.Update,
            RtId = TestRtId,
            CkTypeId = TestRtCkTypeId,
            TargetPath = "$.result",
            AttributeUpdates = new List<AttributeUpdateConfiguration>
            {
                new()
                {
                    AttributeName = "Sections",
                    AttributeValueType = AttributeValueTypesDto.RecordArray,
                    ValuePath = "$.sections"
                }
            }
        };

        var testData = new JsonObject
        {
            ["sections"] = new JsonArray(
                new JsonObject { ["headnig"] = "typo" }) // misspelled property
        };

        var (dataContextReal, nodeContext, meshEtlContext, ckCacheService, next) =
            PrepareTest(config, testData);

        var sectionRecordCkId = new CkId<CkRecordId>("TestModel/Section-1");
        SetupCacheForRecordArrayAttribute(ckCacheService, "Sections", sectionRecordCkId);

        var sectionRecordGraph = BuildRecordGraph("TestModel/Section-1",
            ("Heading", AttributeValueTypesDto.String));
        A.CallTo(() => ckCacheService.GetRtCkRecord(TestTenantId, A<RtCkId<CkRecordId>>._))
            .Returns(sectionRecordGraph);

        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(dataContextReal));
        var node = new CreateUpdateInfoNode(next, meshEtlContext, ckCacheService);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            node.ProcessObjectAsync(dataContext, nodeContext));
    }

    /// <summary>
    /// Configures the cache with a type graph whose single attribute is a RecordArray with the
    /// given declared record type — the precondition for schema-inferred coercion.
    /// </summary>
    private static void SetupCacheForRecordArrayAttribute(ICkCacheService ckCacheService,
        string attributeName, CkId<CkRecordId> valueCkRecordId)
    {
        var attrId = new CkId<CkAttributeId>("TestModel", new CkAttributeId(attributeName));
        var attributeGraph = new CkTypeAttributeGraph(
            ckAttributeId: attrId,
            attributeName: attributeName,
            autoCompleteValues: null,
            valueType: AttributeValueTypesDto.RecordArray,
            valueCkRecordId: valueCkRecordId,
            valueCkEnumId: null,
            autoIncrementReference: null,
            metaData: null,
            defaultValues: null,
            isOptional: true,
            description: null);

        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
        {
            [attrId] = attributeGraph
        };

        var ckTypeGraph = new CkTypeGraph(
            ckTypeId: new CkId<CkTypeId>("TestModel", new CkTypeId("TestType")),
            isAbstract: false,
            isFinal: false,
            isCollectionRoot: false,
            baseTypes: [],
            derivedFromCkTypeId: null,
            definingCollectionRootCkTypeId: null,
            derivedTypes: [],
            definedAttributes: [],
            allAttributes: allAttributes,
            indexes: [],
            associations: new CkGraphDirectedAssociations([]),
            description: string.Empty,
            enableChangeStreamPreAndPostImages: false);

        A.CallTo(() => ckCacheService.TryGetRtCkType(TestTenantId, A<RtCkId<CkTypeId>>._, out ckTypeGraph!))
            .Returns(true)
            .AssignsOutAndRefParameters(ckTypeGraph);
    }

    /// <summary>
    /// Builds a <see cref="CkRecordGraph"/> with the given (name, valueType) attributes.
    /// </summary>
    private static CkRecordGraph BuildRecordGraph(string ckRecordIdString,
        params (string Name, AttributeValueTypesDto ValueType)[] attributes)
    {
        var ckRecordId = new CkId<CkRecordId>(ckRecordIdString);
        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>();
        foreach (var (name, valueType) in attributes)
        {
            var attrId = new CkId<CkAttributeId>("TestModel", new CkAttributeId(name));
            allAttributes[attrId] = new CkTypeAttributeGraph(
                ckAttributeId: attrId,
                attributeName: name,
                autoCompleteValues: null,
                valueType: valueType,
                valueCkRecordId: null,
                valueCkEnumId: null,
                autoIncrementReference: null,
                metaData: null,
                defaultValues: null,
                isOptional: true,
                description: null);
        }

        return new CkRecordGraph(
            ckRecordId: ckRecordId,
            isAbstract: false,
            isFinal: false,
            baseRecords: [],
            derivedFromCkRecordId: null,
            derivedRecords: [],
            definedAttributes: [],
            allAttributes: allAttributes,
            description: string.Empty);
    }

    /// <summary>
    /// Builds a <see cref="CkRecordGraph"/> for a record whose only attribute is a String attribute of
    /// the given name. Used so <see cref="CreateUpdateInfoNode.GetAttributeValue"/> can validate the
    /// inner attribute name and apply its String value to the reconstructed <see cref="RtRecord"/>.
    /// </summary>
    private static CkRecordGraph BuildSingleStringAttributeRecordGraph(string ckRecordIdString,
        string attributeName)
    {
        var ckRecordId = new CkId<CkRecordId>(ckRecordIdString);
        var attrId = new CkId<CkAttributeId>("TestModel", new CkAttributeId(attributeName));
        var attributeGraph = new CkTypeAttributeGraph(
            ckAttributeId: attrId,
            attributeName: attributeName,
            autoCompleteValues: null,
            valueType: AttributeValueTypesDto.String,
            valueCkRecordId: null,
            valueCkEnumId: null,
            autoIncrementReference: null,
            metaData: null,
            defaultValues: null,
            isOptional: true,
            description: null);

        var allAttributes = new Dictionary<CkId<CkAttributeId>, CkTypeAttributeGraph>
        {
            [attrId] = attributeGraph
        };

        return new CkRecordGraph(
            ckRecordId: ckRecordId,
            isAbstract: false,
            isFinal: false,
            baseRecords: [],
            derivedFromCkRecordId: null,
            derivedRecords: [],
            definedAttributes: [],
            allAttributes: allAttributes,
            description: string.Empty);
    }
}
