using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Characterization: the node maps a JSON key/value map into a CK RecordArray. The serialized
/// output (CkRecordId envelope, dynamic Attributes bag, key order, value fidelity, null skip)
/// must be byte-identical to the former hand-built JsonArray/JsonObject construction.
/// </summary>
public class MapToRecordArrayNodeTests
{
    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest(
        MapToRecordArrayNodeConfiguration config, JsonNode testData)
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        IDataContext real = new DataContextImpl(JsonDocument.Parse(testData.ToJsonString()));
        var dataContext = A.Fake<IDataContext>(o => o.Wrapping(real));

        var rootNodeContext =
            NodeContext.CreateRootNodeContext(services.BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("MapToRecordArray", 0, config, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    [Fact]
    public async Task ProcessObjectAsync_MapsToRecordArray_ByteIdenticalToLegacy()
    {
        var config = new MapToRecordArrayNodeConfiguration
        {
            Path = "$.map",
            TargetPath = "$.records",
            CkRecordId = "Loxone/LoxoneState",
            KeyAttributeName = "StateName",
            ValueAttributeName = "StateUuid"
        };

        // String, numeric, and explicit-null values exercise scalar fidelity and the null skip.
        var map = new JsonObject
        {
            ["tempActual"] = "uuid-1",
            ["count"] = 42,
            ["ratio"] = 3.5,
            ["skipped"] = null
        };
        var testData = new JsonObject { ["map"] = map };

        var (dataContext, nodeContext, next) = PrepareTest(config, testData);

        object? captured = null;
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set)
                && call.Arguments.Count >= 2
                && (call.Arguments[0] as string) == config.TargetPath)
            .Invokes(call => captured = call.Arguments[1]);

        var node = new MapToRecordArrayNode(next);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(captured);
        var newJson = JsonSerializer.Serialize(captured, captured!.GetType(), SystemTextJsonOptions.Default);

        var legacy = LegacyBuild(config, (JsonObject)map.DeepClone());
        Assert.Equal(legacy.ToJsonString(SystemTextJsonOptions.Default), newJson);
    }

    private static JsonArray LegacyBuild(MapToRecordArrayNodeConfiguration config, JsonObject map)
    {
        var records = new JsonArray();
        foreach (var kvp in map)
        {
            if (kvp.Value == null) continue;
            records.Add(new JsonObject
            {
                ["CkRecordId"] = new JsonObject
                {
                    ["SemanticVersionedFullName"] = config.CkRecordId
                },
                ["Attributes"] = new JsonObject
                {
                    [config.KeyAttributeName] = kvp.Key,
                    [config.ValueAttributeName] = kvp.Value.DeepClone()
                }
            });
        }
        return records;
    }
}
