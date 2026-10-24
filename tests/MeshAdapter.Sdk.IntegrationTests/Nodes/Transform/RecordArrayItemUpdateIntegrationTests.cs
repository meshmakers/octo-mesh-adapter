using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.Serializer;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Transform;

/// <summary>
/// AB#7442 / AB#7445: parallel pipeline executions that change DIFFERENT states of the same Control no longer lose
/// updates when they use the atomic item update (<c>CreateUpdateInfo@1</c> <c>recordArrayItemUpdates</c> →
/// <c>ApplyChanges@2</c>, no read of the entity). The same executions with the read-modify-write pattern
/// (<c>GetRtEntitiesByType</c> → <c>UpdateRecordArrayItem</c> → whole array) are known-unsafe; that test only reports
/// how many updates were lost and asserts nothing about it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class RecordArrayItemUpdateIntegrationTests(PipelineExecutionFixture fixture)
    : IClassFixture<PipelineExecutionFixture>
{
    private const int Iterations = 50;

    private const string SeedPipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._inserts
            targetValueKind: Array
            updateKind: INSERT
            ckTypeId: MeshAdapterIntegrationTest/TestControl
            rtIdPath: $.rtId
            attributeUpdates:
              - attributeName: ControlName
                attributeValueType: String
                valuePath: $.name
              - attributeName: States
                attributeValueType: RecordArray
                valuePath: $.states
          - type: ApplyChanges@2
            entityUpdatesPath: $._inserts
        """;

    /// <summary>New pattern: one atomic item update per changed state, no read of the Control.</summary>
    private const string AtomicPipeline = """
        transformations:
          - type: CreateUpdateInfo@1
            targetPath: $._updates
            targetValueKind: Array
            updateKind: UPDATE
            ckTypeId: MeshAdapterIntegrationTest/TestControl
            rtIdPath: $.rtId
            recordArrayItemUpdates:
              - attributeName: States
                matchAttributeName: ExternalId
                updatesPath: $.states
                matchValuePath: $.pollUuid
                attributeUpdates:
                  - attributeName: CurrentValue
                    valuePath: $.value
                  - attributeName: LastUpdate
                    value: =now()
          - type: ApplyChanges@2
            entityUpdatesPath: $._updates
        """;

    /// <summary>
    /// Old pattern (known-unsafe under parallel executions): read the Control, change the array in memory
    /// (UpdateRecordArrayItem@2) and write the whole array back. Same shape as the 'Store Control States' pipeline.
    /// </summary>
    private const string ReadModifyWritePipeline = """
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
            fieldFilters:
              - attributePath: ControlName
                operator: Equals
                comparisonValuePath: $.name
        """;

    private static JsonArray States(params string[] ids) => new(ids.Select(id => (JsonNode)new JsonObject
    {
        ["CkRecordId"] = "MeshAdapterIntegrationTest/ControlState",
        ["Attributes"] = new JsonObject { ["ExternalId"] = id, ["CurrentValue"] = "old", ["LastUpdate"] = "never" }
    }).ToArray());

    private static JsonObject Change(string rtId, string name, string poll, string value) => new()
    {
        ["rtId"] = rtId,
        ["name"] = name,
        ["states"] = new JsonArray(new JsonObject { ["pollUuid"] = poll, ["value"] = value })
    };

    private static JsonObject RmwChange(string control, string poll, string value) => new()
    {
        ["states"] = new JsonArray(new JsonObject { ["controlName"] = control, ["pollUuid"] = poll, ["value"] = value })
    };

    [Fact]
    public async Task ParallelExecutions_ChangingDifferentStates_NeverLoseAnUpdate()
    {
        fixture.EnsureInitialized();

        for (var i = 0; i < Iterations; i++)
        {
            var (rtId, name) = await SeedControlAsync("atomic", i);

            using var barrier = new Barrier(2);
            var first = RunParallel(async () =>
            {
                barrier.SignalAndWait();
                await RunAsync(AtomicPipeline, Change(rtId, name, "s1", $"S1-{i}"));
            });
            var second = RunParallel(async () =>
            {
                barrier.SignalAndWait();
                await RunAsync(AtomicPipeline, Change(rtId, name, "s2", $"S2-{i}"));
            });
            await Task.WhenAll(first, second);

            var stored = await ReadStatesAsync(name);
            stored["s1"].Should().Be($"S1-{i}", $"iteration {i}");
            stored["s2"].Should().Be($"S2-{i}", $"iteration {i}");
            stored["s3"].Should().Be("old", $"iteration {i}");
        }
    }

    [Fact]
    public async Task ParallelExecutions_ChangingTheSameState_KeepTheLastWrite_AndLeaveOtherStatesAlone()
    {
        fixture.EnsureInitialized();

        for (var i = 0; i < Iterations; i++)
        {
            var (rtId, name) = await SeedControlAsync("same", i);

            using var barrier = new Barrier(2);
            var tasks = new[] { "first", "second" }.Select(value => RunParallel(async () =>
            {
                barrier.SignalAndWait();
                await RunAsync(AtomicPipeline, Change(rtId, name, "s1", value));
            })).ToArray();
            await Task.WhenAll(tasks);

            var stored = await ReadStatesAsync(name);
            stored["s1"].Should().BeOneOf("first", "second");
            stored["s2"].Should().Be("old");
            stored["s3"].Should().Be("old");
        }
    }

    [Fact]
    public async Task StateThatIsNotInTheArray_IsSkipped_AndNothingIsWritten()
    {
        fixture.EnsureInitialized();
        var (rtId, name) = await SeedControlAsync("missing", 0);

        // The execution succeeds; the engine reports the miss as a warning (covered by the unit tests of
        // BulkRtMutation and ApplyChanges@2) and writes nothing.
        await RunAsync(AtomicPipeline, Change(rtId, name, "does-not-exist", "x"));

        (await ReadStatesAsync(name)).Values.Should().OnlyContain(v => v == "old");
    }

    [Fact]
    public async Task ReadModifyWrite_OfTheWholeArray_IsKnownUnsafeUnderParallelExecutions()
    {
        fixture.EnsureInitialized();

        var lost = 0;
        for (var i = 0; i < Iterations; i++)
        {
            var (rtId, name) = await SeedControlAsync("rmw", i);

            using var barrier = new Barrier(2);
            var first = RunParallel(async () =>
            {
                barrier.SignalAndWait();
                await RunAsync(ReadModifyWritePipeline, RmwChange(name, "s1", "RMW-1"));
            });
            var second = RunParallel(async () =>
            {
                barrier.SignalAndWait();
                await RunAsync(ReadModifyWritePipeline, RmwChange(name, "s2", "RMW-2"));
            });
            await Task.WhenAll(first, second);

            var stored = await ReadStatesAsync(name);
            lost += (stored["s1"] == "RMW-1" ? 0 : 1) + (stored["s2"] == "RMW-2" ? 0 : 1);
        }

        // Documentation, not an assertion: the whole-array write of the later execution overwrites the item the
        // earlier one changed. Use the atomic item update instead (see the tests above).
        TestContext.Current.SendDiagnosticMessage(
            $"read-modify-write lost {lost} of {Iterations * 2} state changes in {Iterations} parallel pairs");
    }

    // ---- helpers ------------------------------------------------------------------------------------------------

    private static Task RunParallel(Func<Task> action) => Task.Run(action, TestContext.Current.CancellationToken);

    private async Task<(string RtId, string Name)> SeedControlAsync(string prefix, int iteration)
    {
        var rtId = OctoObjectId.GenerateNewId().ToString();
        var name = $"{prefix}-{iteration}-{rtId}";
        await RunAsync(SeedPipeline, new JsonObject
        {
            ["rtId"] = rtId,
            ["name"] = name,
            ["states"] = States("s1", "s2", "s3")
        });
        return (rtId, name);
    }

    private async Task<Dictionary<string, string?>> ReadStatesAsync(string name)
    {
        var result = await RunAsync(ReadPipeline, new JsonObject { ["name"] = name });
        var control = ((JsonArray)result["controls"]!["Items"]!).OfType<JsonObject>().Single();
        return ((JsonArray)control["Attributes"]!["States"]!).OfType<JsonObject>().ToDictionary(
            s => s["Attributes"]!["ExternalId"]!.ToString(), s => s["Attributes"]!["CurrentValue"]?.ToString());
    }

    private async Task<JsonObject> RunAsync(string yaml, JsonObject input)
    {
        var systemContext = fixture.GetSystemContext();
        var repository = systemContext.GetSystemTenantRepository();
        await repository.LoadCacheForTenantAsync(fixture.GetService<ICkCacheService>());

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);
        var pipelineId = new OctoObjectId("000000000000000000000098");
        IMeshEtlContext etlContext = new MeshEtlContext(repository.TenantId, repository, pipelineId, Guid.NewGuid(),
            new RtEntityId("System/RtDataPipeline", pipelineId), DateTime.UtcNow, null, globalConfig,
            new Dictionary<string, object?>());

        var serializer = fixture.GetService<IPipelineConfigurationSerializer>();
        var definition = await serializer.DeserializeAsync(yaml);
        var orchestrator = fixture.GetService<IEtlDataOrchestrator>();

        var result = await orchestrator.ExecutePipelineAsync(definition, etlContext, value: input);
        return (JsonObject)(result as JsonNode ?? JsonSerializer.SerializeToNode(result))!;
    }
}
