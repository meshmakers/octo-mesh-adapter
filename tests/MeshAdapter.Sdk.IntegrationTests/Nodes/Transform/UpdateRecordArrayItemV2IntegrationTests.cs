using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
/// AB#7439: a Loxone-like batch of state changes is stored with one read and one write per entity:
/// <c>GroupBy@1 → ForEach → GetRtEntitiesByType → UpdateRecordArrayItem@2 → CreateUpdateInfo → ApplyChanges@2</c>,
/// run through the real pipeline orchestrator against MongoDB.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class UpdateRecordArrayItemV2IntegrationTests(PipelineExecutionFixture fixture)
    : IClassFixture<PipelineExecutionFixture>
{
    private const string ControlA = "6a0e0000000000000b000001";
    private const string ControlB = "6a0e0000000000000b000002";

    private const string SeedPipeline = """
        transformations:
          - type: ForEach@1
            iterationPath: $.controls
            targetPath: $.seeded
            mergePath: $.u
            maxDegreeOfParallelism: 1
            transformations:
              - type: CreateUpdateInfo@1
                targetPath: $.u.insert
                targetValueKind: Array
                updateKind: INSERT
                ckTypeId: MeshAdapterIntegrationTest/TestControl
                rtIdPath: $.key.rtId
                attributeUpdates:
                  - attributeName: ControlName
                    attributeValueType: String
                    valuePath: $.key.name
                  - attributeName: States
                    attributeValueType: RecordArray
                    valuePath: $.key.states
          - type: Flatten@1
            path: $.seeded[*].insert[*]
            targetPath: $._inserts
            targetValueKind: Simple
            targetValueWriteMode: Overwrite
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
        """;

    private const string StorePipeline = """
        transformations:
          - type: GroupBy@1
            path: $.states
            keyPath: $.controlName
            targetPath: $.groups
          - type: ForEach@1
            iterationPath: $.groups
            targetPath: $.groupResults
            mergePath: $.updates
            transformations:
              - type: GetRtEntitiesByType@1
                ckTypeId: MeshAdapterIntegrationTest/TestControl
                targetPath: $.updates._controls
                fieldFilters:
                  - attributePath: ControlName
                    operator: Equals
                    comparisonValuePath: $.key.key
              - type: ForEach@1
                path: $
                fullDocumentPath: $.full
                iterationPath: $.updates._controls.Items
                targetPath: $.updates._controlResults
                mergePath: $.updates
                transformations:
                  - type: UpdateRecordArrayItem@2
                    path: $.key.Attributes.States
                    updatesPath: $.full.key.items
                    matchAttributeName: ExternalId
                    itemMatchValuePath: $.pollUuid
                    targetPath: $.updates._updatedStates
                    attributeUpdates:
                      - attributeName: CurrentValue
                        valuePath: $.value
                      - attributeName: LastUpdate
                        value: =now()
                  - type: CreateUpdateInfo@1
                    targetPath: $.updates._controlUpdate
                    targetValueKind: Array
                    updateKind: UPDATE
                    ckTypeIdPath: $.key.CkTypeId.SemanticVersionedFullName
                    rtIdPath: $.key.RtId
                    attributeUpdates:
                      - attributeName: States
                        attributeValueType: RecordArray
                        valuePath: $.updates._updatedStates
          - type: Flatten@1
            path: $.groupResults[*]._controlResults[*]._controlUpdate[*]
            targetPath: $._updateItems
            targetValueKind: Simple
            targetValueWriteMode: Overwrite
          - type: ApplyChanges@2
            entityUpdatesPath: $._updateItems
        """;

    private const string ReadPipeline = """
        transformations:
          - type: GetRtEntitiesByType@1
            ckTypeId: MeshAdapterIntegrationTest/TestControl
            targetPath: $.controls
        """;

    private static JsonArray States(string prefix) => new(
        Enumerable.Range(1, 5).Select(i => (JsonNode)new JsonObject
        {
            ["CkRecordId"] = "MeshAdapterIntegrationTest/ControlState",
            ["Attributes"] = new JsonObject
            {
                ["ExternalId"] = $"{prefix}{i}", ["CurrentValue"] = "old", ["LastUpdate"] = "never"
            }
        }).ToArray());

    private static JsonObject Change(string control, string poll, string value) => new()
    {
        ["controlName"] = control, ["pollUuid"] = poll, ["value"] = value
    };

    [Fact]
    public async Task BatchOfStateChanges_IsStoredWithOneReadAndOneWritePerEntity()
    {
        fixture.EnsureInitialized();

        await RunAsync(SeedPipeline, new JsonObject
        {
            ["controls"] = new JsonArray(
                new JsonObject { ["rtId"] = ControlA, ["name"] = "A", ["states"] = States("a") },
                new JsonObject { ["rtId"] = ControlB, ["name"] = "B", ["states"] = States("b") })
        });

        // 7 changes: 4 for A (a2 changed twice), 3 for B - interleaved on purpose
        var batch = new JsonObject
        {
            ["states"] = new JsonArray(
                Change("A", "a1", "A1-new"),
                Change("B", "b3", "B3-new"),
                Change("A", "a2", "A2-first"),
                Change("B", "b1", "B1-new"),
                Change("A", "a2", "A2-second"),
                Change("B", "b5", "B5-new"),
                Change("A", "a5", "A5-new"))
        };

        var (_, wrapped) = await RunAsync(StorePipeline, batch);

        // exactly one read per entity (two groups) and one ApplyChanges call with one update per entity
        Fake.GetCalls(wrapped).Count(c => c.Method.Name == "GetRtEntitiesByTypeAsync").Should().Be(2);
        var applyCalls = Fake.GetCalls(wrapped).Where(c => c.Method.Name == "ApplyChangesAsync").ToList();
        applyCalls.Should().HaveCount(1);
        var updates = (IReadOnlyList<IEntityUpdateInfo<RtEntity>>)applyCalls[0].Arguments[1]!;
        updates.Should().HaveCount(2);
        updates.Select(u => u.GetRtEntityId().RtId.ToString()).Should().BeEquivalentTo(ControlA, ControlB);

        // no merge conflict was reported by ApplyChanges@2
        Fake.GetCalls(fixture.PipelineLogger)
            .Where(c => c.Method.Name == nameof(IPipelineLogger.Warning))
            .Select(c => c.Arguments[2]?.ToString() ?? string.Empty)
            .Should().NotContain(m => m.Contains("conflicting values"));

        // all values persisted, the doubled record has the last value, untouched records are unchanged
        var stored = await ReadControlsAsync();
        Value(stored, "A", "a1").Should().Be("A1-new");
        Value(stored, "A", "a2").Should().Be("A2-second");
        Value(stored, "A", "a3").Should().Be("old");
        Value(stored, "A", "a4").Should().Be("old");
        Value(stored, "A", "a5").Should().Be("A5-new");
        Value(stored, "B", "b1").Should().Be("B1-new");
        Value(stored, "B", "b2").Should().Be("old");
        Value(stored, "B", "b3").Should().Be("B3-new");
        Value(stored, "B", "b4").Should().Be("old");
        Value(stored, "B", "b5").Should().Be("B5-new");
    }

    private static string? Value(JsonArray controls, string control, string externalId)
    {
        var entity = controls.OfType<JsonObject>()
            .Single(c => (c["Attributes"] as JsonObject)?["ControlName"]?.ToString() == control);
        return ((JsonArray)entity["Attributes"]!["States"]!).OfType<JsonObject>()
            .Single(s => (s["Attributes"] as JsonObject)?["ExternalId"]?.ToString() == externalId)["Attributes"]!["CurrentValue"]
            ?.ToString();
    }

    private async Task<JsonArray> ReadControlsAsync()
    {
        var (root, _) = await RunAsync(ReadPipeline, new JsonObject());
        return (JsonArray)root["controls"]!["Items"]!;
    }

    private async Task<(JsonObject Root, ITenantRepository Repository)> RunAsync(string yaml, JsonObject input)
    {
        var systemContext = fixture.GetSystemContext();
        var realRepository = systemContext.GetSystemTenantRepository();
        await realRepository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        // Wraps the real repository so the number of reads and ApplyChanges calls can be counted.
        var wrapped = A.Fake<ITenantRepository>(o => o.Wrapping(realRepository));

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        var pipelineId = new OctoObjectId("000000000000000000000098");
        IMeshEtlContext etlContext = new MeshEtlContext(wrapped.TenantId, wrapped, pipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", pipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>());

        var serializer = fixture.GetService<IPipelineConfigurationSerializer>();
        var definition = await serializer.DeserializeAsync(yaml);
        var orchestrator = fixture.GetService<IEtlDataOrchestrator>();

        var result = await orchestrator.ExecutePipelineAsync(definition, etlContext, value: input);
        return ((JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!, wrapped);
    }
}
