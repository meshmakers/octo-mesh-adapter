using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.DependencyInjection;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Base class for node unit tests providing common setup and assertion helpers.
/// </summary>
public abstract class NodeTestBase
{
    protected readonly JsonSerializer Serializer = JsonSerializer.CreateDefault();

    /// <summary>
    /// Prepares a standard test setup with mocked contexts.
    /// </summary>
    protected (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) PrepareTest<TConfig>(
        TConfig config,
        JToken? testData = null)
        where TConfig : class, INodeConfiguration
    {
        var services = new ServiceCollection();
        var logger = A.Fake<IPipelineLogger>();
        var dataContext = A.Fake<IDataContext>();

        A.CallTo(() => dataContext.Current).Returns(testData ?? new JObject());

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
    /// Sets up GetComplexObjectByPath mock to return data from the specified path.
    /// </summary>
    protected void SetupGetComplexObjectByPath<T>(IDataContext dataContext, string path, JToken testData)
        where T : class
    {
        A.CallTo(() => dataContext.GetComplexObjectByPath<T>(path, A<JsonSerializer>._))
            .ReturnsLazily(() =>
            {
                var token = testData.SelectToken(path);
                return token?.ToObject<T>(Serializer);
            });
    }

    /// <summary>
    /// Sets up GetSimpleValueByPath mock to return data from the specified path.
    /// </summary>
    protected void SetupGetSimpleValueByPath<T>(IDataContext dataContext, string path, T value)
    {
        A.CallTo(() => dataContext.GetSimpleValueByPath<T>(path)).Returns(value);
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
