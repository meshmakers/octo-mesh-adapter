using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Base class for node unit tests providing common setup and assertion helpers.
/// </summary>
public abstract class NodeTestBase
{
    protected readonly JsonSerializerOptions Options = SystemTextJsonOptions.Default;

    /// <summary>
    /// Prepares a standard test setup with mocked contexts.
    /// </summary>
    protected (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest<TConfig>(
        TConfig config,
        JsonNode? testData = null)
        where TConfig : class, INodeConfiguration
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        var data = testData ?? new JsonObject();
        A.CallTo(() => dataContext.Get<JsonNode>("$")).Returns(data);

        var rootNodeContext = NodeContext.CreateRootNodeContext(
            services.BuildServiceProvider(),
            logger,
            dataContext);

        var nodeContext = rootNodeContext.RegisterChildNode(
            typeof(TConfig).Name.Replace("Configuration", ""),
            0,
            config,
            dataContext);

        var next = A.Fake<NodeDelegate>();

        return (dataContext, nodeContext, next);
    }

    /// <summary>
    /// Sets up Get&lt;T&gt; mock to return the deserialized data from the specified path.
    /// </summary>
    protected void SetupGetByPath<T>(IDataContext dataContext, string path, JsonNode? testData)
    {
        A.CallTo(() => dataContext.Get<T>(path))
            .ReturnsLazily(() =>
            {
                if (testData == null) return default;
                var node = ResolveSubPath(testData, path);
                if (node == null) return default;
                return node.Deserialize<T>(Options);
            });
    }

    /// <summary>
    /// Sets up Get&lt;T&gt; for simple values at a given path.
    /// </summary>
    protected void SetupGetSimpleValueByPath<T>(IDataContext dataContext, string path, T value)
    {
        A.CallTo(() => dataContext.Get<T>(path)).Returns(value);
    }

    private static JsonNode? ResolveSubPath(JsonNode? root, string? path)
    {
        if (root == null || string.IsNullOrEmpty(path)) return root;
        var p = path.StartsWith("$.") ? path[2..] : path.StartsWith('$') ? path[1..] : path;
        if (string.IsNullOrEmpty(p)) return root;
        JsonNode? cursor = root;
        foreach (var segment in p.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            if (cursor is JsonObject obj && obj.TryGetPropertyValue(segment, out var next))
            {
                cursor = next;
            }
            else
            {
                return null;
            }
        }
        return cursor;
    }

    /// <summary>
    /// Verifies that the next delegate was called exactly once.
    /// </summary>
    protected static void VerifyNextCalled(NodeDelegate next, IDataContext dataContext, INodeContext nodeContext)
    {
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    /// <summary>
    /// Verifies that the next delegate was not called.
    /// </summary>
    protected static void VerifyNextNotCalled(NodeDelegate next, IDataContext dataContext, INodeContext nodeContext)
    {
        A.CallTo(() => next(dataContext, nodeContext)).MustNotHaveHappened();
    }
}
