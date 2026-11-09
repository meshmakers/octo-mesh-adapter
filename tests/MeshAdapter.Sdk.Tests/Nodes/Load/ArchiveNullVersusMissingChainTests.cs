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
            ("Measured", AttributeValueTypesDto.Double),
            ("Cleared", AttributeValueTypesDto.Double),
            ("NotSent", AttributeValueTypesDto.Double),
            ("From", AttributeValueTypesDto.DateTime),
            ("To", AttributeValueTypesDto.DateTime));
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

    private async Task RunCreateUpdateInfoAsync(IDataContext dataContext, bool withWindow)
    {
        var updates = new List<AttributeUpdateConfiguration>
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
            UpdateKind = UpdateKind.Insert,
            RtId = SourceRtId,
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

    private void SetupCkType(params (string Name, AttributeValueTypesDto Type)[] attributes)
    {
        var allAttributes = attributes.ToDictionary(
            a => new CkId<CkAttributeId>("TestModel", new CkAttributeId(a.Name)),
            a => new CkTypeAttributeGraph(
                ckAttributeId: new CkId<CkAttributeId>("TestModel", new CkAttributeId(a.Name)),
                attributeName: a.Name,
                autoCompleteValues: null,
                valueType: a.Type,
                valueCkRecordId: null,
                valueCkEnumId: null,
                autoIncrementReference: null,
                metaData: null,
                defaultValues: null,
                isOptional: true,
                description: null));

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
