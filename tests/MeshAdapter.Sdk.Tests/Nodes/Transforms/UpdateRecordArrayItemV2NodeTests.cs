using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

public class UpdateRecordArrayItemV2NodeTests
{
    private const string States = """
        "states": [
            { "Attributes": { "ExternalId": "A", "CurrentValue": 1, "LastUpdate": "old" } },
            { "Attributes": { "ExternalId": "B", "CurrentValue": 2, "LastUpdate": "old" } },
            { "Attributes": { "ExternalId": "C", "CurrentValue": 3, "LastUpdate": "old" } }
        ]
        """;

    private static IDataContext MakeContext(string json) =>
        new DataContextImpl(JsonDocument.Parse(json));

    private static (INodeContext nodeContext, NodeDelegate next, IPipelineLogger logger) Prepare(
        IDataContext dataContext, UpdateRecordArrayItemV2NodeConfiguration config)
    {
        var logger = A.Fake<IPipelineLogger>();
        var root = NodeContext.CreateRootNodeContext(new ServiceCollection().BuildServiceProvider(), logger,
            dataContext);
        var nodeContext = root.RegisterChildNode("UpdateRecordArrayItem", 0, config, dataContext);
        return (nodeContext, A.Fake<NodeDelegate>(), logger);
    }

    private static UpdateRecordArrayItemV2NodeConfiguration Config(
        string? updatesPath = "$.updates", string target = "$.result", params RecordAttributeUpdate[] updates) =>
        new()
        {
            Path = "$.states",
            TargetPath = target,
            UpdatesPath = updatesPath!,
            MatchAttributeName = "ExternalId",
            ItemMatchValuePath = "$.id",
            AttributeUpdates = updates.Length > 0
                ? updates
                : [new RecordAttributeUpdate { AttributeName = "CurrentValue", ValuePath = "$.value" }]
        };

    private static async Task<(IDataContext ctx, INodeContext nc, NodeDelegate next)> RunAsync(
        string json, UpdateRecordArrayItemV2NodeConfiguration config)
    {
        var ctx = MakeContext(json);
        var (nc, next, _) = Prepare(ctx, config);
        await new UpdateRecordArrayItemV2Node(next).ProcessObjectAsync(ctx, nc);
        return (ctx, nc, next);
    }

    [Fact]
    public async Task MultipleUpdates_AreAppliedInOnePass_UntouchedRecordsKeepOrderAndValue()
    {
        var json = "{" + States + """, "updates": [ { "id": "C", "value": 30 }, { "id": "A", "value": 10 } ] }""";

        var (ctx, nc, next) = await RunAsync(json, Config());

        Assert.Equal(3, ctx.Length("$.result"));
        Assert.Equal("A", ctx.Get<string>("$.result[0].Attributes.ExternalId"));
        Assert.Equal(10, ctx.Get<int>("$.result[0].Attributes.CurrentValue"));
        Assert.Equal(2, ctx.Get<int>("$.result[1].Attributes.CurrentValue"));
        Assert.Equal(30, ctx.Get<int>("$.result[2].Attributes.CurrentValue"));
        Assert.Equal("old", ctx.Get<string>("$.result[1].Attributes.LastUpdate"));
        // source array is not modified
        Assert.Equal(1, ctx.Get<int>("$.states[0].Attributes.CurrentValue"));
        A.CallTo(() => next.Invoke(ctx, nc)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task DuplicateKey_LastUpdateWinsPerAttribute()
    {
        var json = "{" + States + """, "updates": [ { "id": "B", "value": 21 }, { "id": "b", "value": 22 } ] }""";

        var (ctx, _, _) = await RunAsync(json, Config());

        Assert.Equal(22, ctx.Get<int>("$.result[1].Attributes.CurrentValue"));
    }

    [Fact]
    public async Task UnmatchedUpdate_IsSkippedAndReportedOnceWithCountOnly()
    {
        var json = "{" + States + """, "updates": [ { "id": "A", "value": 10 }, { "id": "X", "value": 99 }, { "id": "Y", "value": 98 }, { "value": 97 } ] }""";
        var ctx = MakeContext(json);
        var (nc, next, logger) = Prepare(ctx, Config());

        await new UpdateRecordArrayItemV2Node(next).ProcessObjectAsync(ctx, nc);

        Assert.Equal(10, ctx.Get<int>("$.result[0].Attributes.CurrentValue"));
        Assert.Equal(3, ctx.Length("$.result"));
        A.CallTo(logger).Where(call => call.Method.Name == nameof(IPipelineLogger.Warning)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task NoUpdateMatches_NothingIsWrittenToTargetPath()
    {
        var json = "{" + States + """, "updates": [ { "id": "X", "value": 99 } ] }""";

        var (ctx, nc, next) = await RunAsync(json, Config());

        Assert.Equal(DataKind.Undefined, ctx.GetKind("$.result"));
        A.CallTo(() => next.Invoke(ctx, nc)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EmptyUpdatesArray_NothingIsWritten()
    {
        var json = "{" + States + """, "updates": [] }""";

        var (ctx, nc, next) = await RunAsync(json, Config());

        Assert.Equal(DataKind.Undefined, ctx.GetKind("$.result"));
        A.CallTo(() => next.Invoke(ctx, nc)).MustHaveHappenedOnceExactly();
    }

    [Theory]
    [InlineData("""{ "states": { "a": 1 }, "updates": [ { "id": "A", "value": 1 } ] }""")]
    [InlineData("""{ "updates": [ { "id": "A", "value": 1 } ] }""")]
    [InlineData("""{ "states": [ { "Attributes": { "ExternalId": "A" } } ], "updates": { "id": "A" } }""")]
    public async Task SourceOrUpdatesNotAnArray_WritesNothingAndContinues(string json)
    {
        var (ctx, nc, next) = await RunAsync(json, Config());

        Assert.Equal(DataKind.Undefined, ctx.GetKind("$.result"));
        A.CallTo(() => next.Invoke(ctx, nc)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StaticValueNowAndValuePath_AreSupported()
    {
        var json = "{" + States + """, "updates": [ { "id": "A", "value": 7 } ] }""";
        var config = Config(null!, "$.result",
            new RecordAttributeUpdate { AttributeName = "CurrentValue", ValuePath = "$.value" },
            new RecordAttributeUpdate { AttributeName = "LastUpdate", Value = "=now()" },
            new RecordAttributeUpdate { AttributeName = "Quality", Value = "Good" });
        config.UpdatesPath = "$.updates";

        var (ctx, _, _) = await RunAsync(json, config);

        Assert.Equal(7, ctx.Get<int>("$.result[0].Attributes.CurrentValue"));
        Assert.Equal("Good", ctx.Get<string>("$.result[0].Attributes.Quality"));
        Assert.True(DateTime.TryParse(ctx.Get<string>("$.result[0].Attributes.LastUpdate"), out var ts));
        Assert.True((DateTime.UtcNow - ts.ToUniversalTime()).Duration() < TimeSpan.FromMinutes(1));
    }

    [Fact]
    public async Task CamelCaseKeys_AreMatchedAndUpdatedWithoutDuplicates()
    {
        const string json = """
            {
                "states": [ { "attributes": { "externalId": "A", "currentValue": 1 } } ],
                "updates": [ { "id": "a", "value": 5 } ]
            }
            """;

        var (ctx, _, _) = await RunAsync(json, Config());

        Assert.Equal(5, ctx.Get<int>("$.result[0].attributes.currentValue"));
        Assert.Equal(["externalId", "currentValue"], ctx.Keys("$.result[0].attributes").ToArray());
    }

    [Fact]
    public async Task NumericMatchValue_IsComparedAsText()
    {
        const string json = """
            {
                "states": [ { "Attributes": { "ExternalId": "42", "CurrentValue": 1 } } ],
                "updates": [ { "id": 42, "value": 5 } ]
            }
            """;

        var (ctx, _, _) = await RunAsync(json, Config());

        Assert.Equal(5, ctx.Get<int>("$.result[0].Attributes.CurrentValue"));
    }
}
