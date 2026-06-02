using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Regression test for review finding #7:
///
/// The pre-migration Newtonsoft version of UpdateRecordArrayItemNode early-returned
/// (without invoking <c>dataContext.Set(TargetPath, ...)</c>) when no record matched
/// the configured key. The STJ rewrite (commit 71ff0fa) accidentally dropped that
/// early-return — the node now always writes the cloned, unchanged array to
/// <c>TargetPath</c>, which materializes an artifact at <c>TargetPath</c> when
/// <c>TargetPath != Path</c> and a downstream reader had previously relied on
/// the absence to signal "no update".
/// </summary>
public class UpdateRecordArrayItemNodeTests
{
    private static IDataContext MakeContext(string json) =>
        new DataContextImpl(JsonDocument.Parse(json));

    private static (INodeContext nodeContext, NodeDelegate next) PrepareNode<TConfig>(
        IDataContext dataContext, TConfig config, string nodeName) where TConfig : INodeConfiguration
    {
        var logger = A.Fake<IPipelineLogger>();
        var rootNodeContext = NodeContext.CreateRootNodeContext(
            new ServiceCollection().BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode(nodeName, 0, config!, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (nodeContext, next);
    }

    [Fact]
    public async Task ProcessObjectAsync_NoMatchAndDistinctTargetPath_DoesNotWriteTargetPath()
    {
        // RecordArray at $.records with one record whose "Key" attribute is "EXISTING".
        // We search for "MISSING" — no match — and expect TargetPath ($.result) to
        // remain absent. Pre-fix, the node wrote the cloned array to $.result anyway.
        const string json = """
            {
                "records": [
                    { "Attributes": { "Key": "EXISTING", "Value": "old" } }
                ]
            }
            """;
        using var dataContext = MakeContext(json);

        var config = new UpdateRecordArrayItemNodeConfiguration
        {
            Path = "$.records",
            TargetPath = "$.result",
            MatchAttributeName = "Key",
            MatchValue = "MISSING",
            AttributeUpdates = new List<RecordAttributeUpdate>
            {
                new() { AttributeName = "Value", Value = "new" }
            }
        };
        var (nodeContext, next) = PrepareNode(dataContext, config, "UpdateRecordArrayItem");
        var node = new UpdateRecordArrayItemNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(DataKind.Undefined, dataContext.GetKind("$.result"));
        // next() should still be invoked so downstream nodes execute normally.
        A.CallTo(() => next.Invoke(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ProcessObjectAsync_MatchFound_WritesUpdatedArrayToTargetPath()
    {
        // Sanity check the happy path: when a match exists, the cloned array (with
        // the matched record updated) IS written to TargetPath.
        const string json = """
            {
                "records": [
                    { "Attributes": { "Key": "K1", "Value": "old" } }
                ]
            }
            """;
        using var dataContext = MakeContext(json);

        var config = new UpdateRecordArrayItemNodeConfiguration
        {
            Path = "$.records",
            TargetPath = "$.result",
            MatchAttributeName = "Key",
            MatchValue = "K1",
            AttributeUpdates = new List<RecordAttributeUpdate>
            {
                new() { AttributeName = "Value", Value = "new" }
            }
        };
        var (nodeContext, next) = PrepareNode(dataContext, config, "UpdateRecordArrayItem");
        var node = new UpdateRecordArrayItemNode(next);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.Equal(DataKind.Array, dataContext.GetKind("$.result"));
        Assert.Equal("new", dataContext.Get<string>("$.result[0].Attributes.Value"));
        A.CallTo(() => next.Invoke(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }
}
