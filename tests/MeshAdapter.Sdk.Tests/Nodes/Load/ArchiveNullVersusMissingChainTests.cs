using System.Text.Json;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts.DependencyGraph;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Contracts.StreamData;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// AB#8622: pins that an explicit null and a missing attribute stay distinguishable along the whole
/// node chain — document → <c>CreateUpdateInfo</c> → data context → <c>SaveStreamDataInArchive</c> /
/// <c>SaveTimeRangeStreamDataInArchive</c> → the point handed to the archive.
/// </summary>
/// <remarks>
/// The archive keeps the stored value of a column a point does not name and clears a column named with
/// null. So a JSON null must arrive as a key with a null value and a missing property as no key at
/// all. Both nodes run against one real <see cref="DataContextImpl" />, so the hand-over through the
/// data context (which serialises the update infos) is part of what is tested, not mocked away.
/// </remarks>
public class ArchiveNullVersusMissingChainTests : SessionNodeTestBase
{
    private const string TenantId = "test-tenant";
    private const string UpdateInfosPath = "$.updateInfos";
    private const string ArchiveRtIdString = "65d5c447b420da3fb12381bc";
    private static readonly OctoObjectId ArchiveRtId = new(ArchiveRtIdString);
    private static readonly OctoObjectId SourceRtId = new("000000000000000000000001");
    private static readonly RtCkId<CkTypeId> CkType = new("TestModel/TestType");
    private static readonly CkId<CkEnumId> QualityEnum = new("TestModel", new CkEnumId("Quality"));
    private static readonly CkId<CkRecordId> AmountRecord = new("TestModel", new CkRecordId("Amount"));

    private readonly ISystemContext _systemContext = A.Fake<ISystemContext>();
    private readonly IStreamDataRepository _streamDataRepository = A.Fake<IStreamDataRepository>();

    public ArchiveNullVersusMissingChainTests()
    {
        A.CallTo(() => EtlContext.TenantId).Returns(TenantId);
        A.CallTo(() => EtlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => EtlContext.TransactionStartedDateTime)
            .Returns(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));

        var tenantContext = A.Fake<ITenantContext>();
        A.CallTo(() => _systemContext.FindTenantContextAsync(TenantId)).Returns(Task.FromResult(tenantContext));
        A.CallTo(() => tenantContext.GetStreamDataRepository()).Returns(_streamDataRepository);

        SetupCkType(
            Attribute("Measured", AttributeValueTypesDto.Double),
            Attribute("Cleared", AttributeValueTypesDto.Double),
            Attribute("NotSent", AttributeValueTypesDto.Double),
            Attribute("From", AttributeValueTypesDto.DateTime),
            Attribute("To", AttributeValueTypesDto.DateTime),
            // AB#8625: an optional and a required enum column (e.g. a quality code) and a record column.
            Attribute("Quality", AttributeValueTypesDto.Enum, valueCkEnumId: QualityEnum),
            Attribute("RequiredQuality", AttributeValueTypesDto.Enum, valueCkEnumId: QualityEnum, isOptional: false),
            Attribute("Amount", AttributeValueTypesDto.Record, valueCkRecordId: AmountRecord));
        SetupAmountRecord();
    }

    /// <summary>
    /// The document a producer delivers: one value, one explicit null, and no property at all for the
    /// third column the pipeline maps.
    /// </summary>
    private const string Document =
        """
        { "measured": 1.5, "cleared": null,
          "from": "2026-01-01T00:00:00Z", "to": "2026-01-01T00:15:00Z" }
        """;

    [Fact]
    public async Task RawSave_KeepsNullValueAndMissingApart()
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(Document));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: false);

        List<StreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .Invokes((OctoObjectId _, IEnumerable<StreamDataPoint> points) => captured = points.ToList());

        var config = new SaveStreamDataInArchiveNodeConfiguration
        {
            Path = UpdateInfosPath,
            ArchiveRtId = ArchiveRtIdString
        };
        var node = new SaveStreamDataInArchiveNode(A.Fake<NodeDelegate>(), EtlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, NodeContextFor("SaveStreamDataInArchive", config, dataContext));

        var point = Assert.Single(captured!);
        AssertNullValueAndMissingApart(point.Attributes);
    }

    [Fact]
    public async Task TimeRangeSave_KeepsNullValueAndMissingApart()
    {
        GivenSystemSessionIsExpected();
        SetupExistingSource();

        var dataContext = new DataContextImpl(JsonDocument.Parse(Document));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: true);

        List<TimeRangeStreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Invokes((OctoObjectId _, IEnumerable<TimeRangeStreamDataPoint> points, CancellationToken _) =>
                captured = points.ToList());

        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = UpdateInfosPath,
            ArchiveRtId = ArchiveRtIdString
        };
        var node = new SaveTimeRangeStreamDataInArchiveNode(A.Fake<NodeDelegate>(), EtlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext,
            NodeContextFor("SaveTimeRangeStreamDataInArchive", config, dataContext));

        var point = Assert.Single(captured!);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), point.From);
        Assert.Equal(new DateTime(2026, 1, 1, 0, 15, 0, DateTimeKind.Utc), point.To);
        Assert.False(point.Attributes.ContainsKey("From"));
        Assert.False(point.Attributes.ContainsKey("To"));
        AssertNullValueAndMissingApart(point.Attributes);
    }

    private static void AssertNullValueAndMissingApart(IReadOnlyDictionary<string, object?> attributes)
    {
        // A value stays a value.
        Assert.Equal(1.5, Convert.ToDouble(attributes["Measured"]));

        // An explicit null stays a named column with a null value: the archive clears it.
        Assert.True(attributes.ContainsKey("Cleared"), "an explicit null must reach the archive as a named column");
        Assert.Null(attributes["Cleared"]);

        // A missing property stays unnamed: the archive keeps the stored value.
        Assert.False(attributes.ContainsKey("NotSent"), "a missing property must not reach the archive as a column");
    }

    /// <summary>
    /// AB#8625: a document with an explicit null for an enum column and for a member of a record the
    /// update does not hold yet; the second record member and a third column are not sent.
    /// </summary>
    private const string EnumAndRecordDocument =
        """
        { "quality": null, "amountValue": null,
          "from": "2026-01-01T00:00:00Z", "to": "2026-01-01T00:15:00Z" }
        """;

    private static readonly List<AttributeUpdateConfiguration> EnumAndRecordUpdates =
    [
        new() { AttributeName = "Quality", AttributeValueType = AttributeValueTypesDto.Enum, ValuePath = "$.quality" },
        new() { AttributeName = "Amount.Value", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.amountValue" },
        new() { AttributeName = "Amount.Unit", AttributeValueType = AttributeValueTypesDto.String, ValuePath = "$.amountUnit" },
        new() { AttributeName = "NotSent", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.notSent" }
    ];

    [Fact]
    public async Task RawSave_ExplicitNullOnEnumAndRecordSubPath_ReachesTheArchiveAsNull()
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(EnumAndRecordDocument));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: false, EnumAndRecordUpdates,
            explicitNullCreatesRecords: true);

        List<StreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .Invokes((OctoObjectId _, IEnumerable<StreamDataPoint> points) => captured = points.ToList());

        var config = new SaveStreamDataInArchiveNodeConfiguration
        {
            Path = UpdateInfosPath,
            ArchiveRtId = ArchiveRtIdString
        };
        var node = new SaveStreamDataInArchiveNode(A.Fake<NodeDelegate>(), EtlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, NodeContextFor("SaveStreamDataInArchive", config, dataContext));

        var point = Assert.Single(captured!);
        AssertEnumAndRecordNullsApart(point.Attributes);
    }

    [Fact]
    public async Task TimeRangeSave_ExplicitNullOnEnumAndRecordSubPath_ReachesTheArchiveAsNull()
    {
        GivenSystemSessionIsExpected();
        SetupExistingSource();

        var dataContext = new DataContextImpl(JsonDocument.Parse(EnumAndRecordDocument));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: true, EnumAndRecordUpdates,
            explicitNullCreatesRecords: true);

        List<TimeRangeStreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertTimeRangeAsync(
                ArchiveRtId, A<IEnumerable<TimeRangeStreamDataPoint>>._, A<CancellationToken>._))
            .Invokes((OctoObjectId _, IEnumerable<TimeRangeStreamDataPoint> points, CancellationToken _) =>
                captured = points.ToList());

        var config = new SaveTimeRangeStreamDataInArchiveNodeConfiguration
        {
            Path = UpdateInfosPath,
            ArchiveRtId = ArchiveRtIdString
        };
        var node = new SaveTimeRangeStreamDataInArchiveNode(A.Fake<NodeDelegate>(), EtlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext,
            NodeContextFor("SaveTimeRangeStreamDataInArchive", config, dataContext));

        var point = Assert.Single(captured!);
        AssertEnumAndRecordNullsApart(point.Attributes);
    }

    [Fact]
    public async Task RawSave_RecordSubPathNotSent_KeepsTheRecordMissing()
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse(Document));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: false,
        [
            new() { AttributeName = "Measured", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.measured" },
            new() { AttributeName = "Amount.Value", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.amountValue" },
            new() { AttributeName = "Quality", AttributeValueType = AttributeValueTypesDto.Enum, ValuePath = "$.quality" }
        ], explicitNullCreatesRecords: true);

        List<StreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .Invokes((OctoObjectId _, IEnumerable<StreamDataPoint> points) => captured = points.ToList());

        var config = new SaveStreamDataInArchiveNodeConfiguration
        {
            Path = UpdateInfosPath,
            ArchiveRtId = ArchiveRtIdString
        };
        var node = new SaveStreamDataInArchiveNode(A.Fake<NodeDelegate>(), EtlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, NodeContextFor("SaveStreamDataInArchive", config, dataContext));

        var point = Assert.Single(captured!);
        Assert.Equal(1.5, Convert.ToDouble(point.Attributes["Measured"]));
        Assert.False(point.Attributes.ContainsKey("Amount"), "a record whose members are not sent must stay missing");
        Assert.False(point.Attributes.ContainsKey("Quality"), "an enum that is not sent must stay missing");
    }

    [Fact]
    public async Task RawSave_ExplicitNullOnRecordSubPath_Default_DropsTheNullAndKeepsTheRecordMissing()
    {
        // ExplicitNullCreatesRecords is off by default: a null on a member of a record the update does not hold
        // is dropped as before AB#8625; an explicit null on an optional enum is still a named null.
        var dataContext = new DataContextImpl(JsonDocument.Parse(EnumAndRecordDocument));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: false, EnumAndRecordUpdates);

        List<StreamDataPoint>? captured = null;
        A.CallTo(() => _streamDataRepository.InsertAsync(ArchiveRtId, A<IEnumerable<StreamDataPoint>>._))
            .Invokes((OctoObjectId _, IEnumerable<StreamDataPoint> points) => captured = points.ToList());

        var config = new SaveStreamDataInArchiveNodeConfiguration
        {
            Path = UpdateInfosPath,
            ArchiveRtId = ArchiveRtIdString
        };
        var node = new SaveStreamDataInArchiveNode(A.Fake<NodeDelegate>(), EtlContext, _systemContext);
        await node.ProcessObjectAsync(dataContext, NodeContextFor("SaveStreamDataInArchive", config, dataContext));

        var point = Assert.Single(captured!);
        Assert.False(point.Attributes.ContainsKey("Amount"), "without the opt-in the null must not create the record");
        Assert.True(point.Attributes.ContainsKey("Quality"));
        Assert.Null(point.Attributes["Quality"]);
        Assert.False(point.Attributes.ContainsKey("NotSent"));
    }

    [Fact]
    public async Task EntityUpdate_ExplicitNullOnRecordSubPath_Default_LeavesTheStoredRecordAlone()
    {
        // An UPDATE of a runtime entity replaces every record it names. With the default the null on Amount.Value
        // does not put an Amount into the update, so the stored record keeps all its members.
        var dataContext = new DataContextImpl(JsonDocument.Parse("""{ "measured": 2.5, "amountValue": null }"""));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: false,
        [
            new() { AttributeName = "Measured", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.measured" },
            new() { AttributeName = "Amount.Value", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.amountValue" }
        ], updateKind: UpdateKind.Update);

        var update = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(UpdateInfosPath)!);
        Assert.NotNull(update.RtEntity);
        Assert.Equal(2.5, Convert.ToDouble(update.RtEntity.Attributes["Measured"]));
        Assert.False(update.RtEntity.Attributes.ContainsKey("Amount"),
            "without the opt-in an entity update must not carry a record that holds only the null");
    }

    [Fact]
    public async Task EntityUpdate_ExplicitNullOnRecordSubPath_OptIn_CarriesTheRecordWithTheNull()
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse("""{ "amountValue": null }"""));
        await RunCreateUpdateInfoAsync(dataContext, withWindow: false,
        [
            new() { AttributeName = "Amount.Value", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.amountValue" }
        ], updateKind: UpdateKind.Update, explicitNullCreatesRecords: true);

        var update = Assert.Single(dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(UpdateInfosPath)!);
        var amount = Assert.IsType<RtRecord>(update.RtEntity!.Attributes["Amount"]);
        Assert.True(amount.Attributes.ContainsKey("Value"));
        Assert.Null(amount.Attributes["Value"]);
    }

    [Fact]
    public async Task CreateUpdateInfo_ExplicitNullOnRequiredEnum_FailsWithThePath()
    {
        var dataContext = new DataContextImpl(JsonDocument.Parse("""{ "requiredQuality": null }"""));

        var ex = await Assert.ThrowsAsync<InvalidPathException>(() => RunCreateUpdateInfoAsync(dataContext,
            withWindow: false,
            [
                new()
                {
                    AttributeName = "RequiredQuality", AttributeValueType = AttributeValueTypesDto.Enum,
                    ValuePath = "$.requiredQuality"
                }
            ]));

        Assert.Contains("'requiredQuality' cannot be set to null", ex.Message);
        Assert.Contains("is not optional", ex.Message);
    }

    private static void AssertEnumAndRecordNullsApart(IReadOnlyDictionary<string, object?> attributes)
    {
        // An explicit null on an optional enum column stays a named column with a null value.
        Assert.True(attributes.ContainsKey("Quality"), "an explicit null on an enum must reach the archive as a named column");
        Assert.Null(attributes["Quality"]);

        // An explicit null on a member of a record that did not exist: the record is there, the member is a
        // named null, the member that was not sent stays unnamed.
        Assert.True(attributes.ContainsKey("Amount"), "an explicit null on a record member must create the record");
        var amount = Assert.IsType<RtRecord>(attributes["Amount"]);
        Assert.True(amount.Attributes.ContainsKey("Value"), "the explicit null must be a named record member");
        Assert.Null(amount.Attributes["Value"]);
        Assert.False(amount.Attributes.ContainsKey("Unit"), "a record member that is not sent must not be named");

        // A missing property stays unnamed.
        Assert.False(attributes.ContainsKey("NotSent"), "a missing property must not reach the archive as a column");
    }

    private async Task RunCreateUpdateInfoAsync(IDataContext dataContext, bool withWindow,
        List<AttributeUpdateConfiguration>? attributeUpdates = null, UpdateKind updateKind = UpdateKind.Insert,
        bool explicitNullCreatesRecords = false)
    {
        var updates = attributeUpdates?.ToList() ?? new List<AttributeUpdateConfiguration>
        {
            new() { AttributeName = "Measured", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.measured" },
            new() { AttributeName = "Cleared", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.cleared" },
            new() { AttributeName = "NotSent", AttributeValueType = AttributeValueTypesDto.Double, ValuePath = "$.notSent" }
        };
        if (withWindow)
        {
            updates.Add(new AttributeUpdateConfiguration
            {
                AttributeName = "From", AttributeValueType = AttributeValueTypesDto.DateTime, ValuePath = "$.from"
            });
            updates.Add(new AttributeUpdateConfiguration
            {
                AttributeName = "To", AttributeValueType = AttributeValueTypesDto.DateTime, ValuePath = "$.to"
            });
        }

        var config = new CreateUpdateInfoNodeConfiguration
        {
            UpdateKind = updateKind,
            RtId = SourceRtId,
            ExplicitNullCreatesRecords = explicitNullCreatesRecords,
            CkTypeId = CkType,
            TargetPath = UpdateInfosPath,
            // How the shipped pipelines collect update infos for a save node.
            TargetValueKind = ValueKinds.Array,
            TargetValueWriteMode = TargetValueWriteModes.Append,
            AttributeUpdates = updates
        };

        var node = new CreateUpdateInfoNode(A.Fake<NodeDelegate>(), EtlContext, CkCache);
        await node.ProcessObjectAsync(dataContext, NodeContextFor("CreateUpdateInfo", config, dataContext));
    }

    private static INodeContext NodeContextFor(string name, INodeConfiguration config, IDataContext dataContext)
    {
        var root = NodeContext.CreateRootNodeContext(
            new ServiceCollection().BuildServiceProvider(), A.Fake<IPipelineLogger>(), dataContext);
        return root.RegisterChildNode(name, 0, config, dataContext);
    }

    private void SetupExistingSource()
    {
        var resultSet = A.Fake<IResultSet<RtEntity>>();
        var entities = new List<RtEntity> { new(CkType, SourceRtId) };
        A.CallTo(() => resultSet.Items).Returns(entities);
        A.CallTo(() => resultSet.TotalCount).Returns(entities.Count);
        A.CallTo(() => TenantRepository.GetRtEntitiesByIdAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<IReadOnlyList<OctoObjectId>>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Returns(Task.FromResult(resultSet));
    }

    private static CkTypeAttributeGraph Attribute(string name, AttributeValueTypesDto type,
        CkId<CkRecordId>? valueCkRecordId = null, CkId<CkEnumId>? valueCkEnumId = null, bool isOptional = true) =>
        new(
            ckAttributeId: new CkId<CkAttributeId>("TestModel", new CkAttributeId(name)),
            attributeName: name,
            autoCompleteValues: null,
            valueType: type,
            valueCkRecordId: valueCkRecordId,
            valueCkEnumId: valueCkEnumId,
            autoIncrementReference: null,
            metaData: null,
            defaultValues: null,
            isOptional: isOptional,
            description: null);

    private void SetupAmountRecord()
    {
        var members = new[]
        {
            Attribute("Value", AttributeValueTypesDto.Double),
            Attribute("Unit", AttributeValueTypesDto.String)
        }.ToDictionary(a => a.CkAttributeId, a => a);
        var recordGraph = new CkRecordGraph(AmountRecord, isAbstract: false, isFinal: false, baseRecords: [],
            derivedFromCkRecordId: null, derivedRecords: [], definedAttributes: [], allAttributes: members,
            description: string.Empty);

        A.CallTo(() => CkCache.TryGetCkRecord(TenantId, A<CkId<CkRecordId>>._, out recordGraph!))
            .Returns(true)
            .AssignsOutAndRefParameters(recordGraph);
        A.CallTo(() => CkCache.TryGetRtCkRecord(TenantId, A<RtCkId<CkRecordId>>._, out recordGraph!))
            .Returns(true)
            .AssignsOutAndRefParameters(recordGraph);
    }

    private void SetupCkType(params CkTypeAttributeGraph[] attributes)
    {
        var allAttributes = attributes.ToDictionary(a => a.CkAttributeId, a => a);

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

        A.CallTo(() => CkCache.TryGetRtCkType(TenantId, A<RtCkId<CkTypeId>>._, out ckTypeGraph!))
            .Returns(true)
            .AssignsOutAndRefParameters(ckTypeGraph);
    }
}
