using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Helpers;

/// <summary>
/// Factory for creating mock objects used in unit tests.
/// </summary>
public static class MockFactory
{
    /// <summary>
    /// Creates a fake IDataContext with optional initial data.
    /// </summary>
    public static IDataContext CreateDataContext(JsonNode? current = null)
    {
        var dataContext = A.Fake<IDataContext>();
        var data = current ?? new JsonObject();
        A.CallTo(() => dataContext.Get<JsonNode>("$")).Returns(data);
        return dataContext;
    }

    /// <summary>
    /// Creates a fake IPipelineLogger.
    /// </summary>
    public static IPipelineLogger CreatePipelineLogger()
    {
        return A.Fake<IPipelineLogger>();
    }

    /// <summary>
    /// Creates a fake NodeDelegate that can be verified.
    /// </summary>
    public static NodeDelegate CreateNodeDelegate()
    {
        return A.Fake<NodeDelegate>();
    }

    /// <summary>
    /// Creates a node context with the specified configuration.
    /// </summary>
    public static INodeContext CreateNodeContext<TConfig>(
        TConfig configuration,
        IDataContext? dataContext = null,
        IServiceProvider? serviceProvider = null)
        where TConfig : class, INodeConfiguration
    {
        var services = new ServiceCollection();
        var logger = CreatePipelineLogger();
        var dc = dataContext ?? CreateDataContext();

        var rootContext = NodeContext.CreateRootNodeContext(
            serviceProvider ?? services.BuildServiceProvider(),
            logger,
            dc);

        return rootContext.RegisterChildNode(
            typeof(TConfig).Name.Replace("Configuration", ""),
            0,
            configuration,
            dc);
    }

    /// <summary>
    /// Creates a complete test setup tuple for node testing.
    /// </summary>
    public static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next) CreateTestSetup<TConfig>(
        TConfig configuration,
        JsonNode? testData = null)
        where TConfig : class, INodeConfiguration
    {
        var dataContext = CreateDataContext(testData);
        var nodeContext = CreateNodeContext(configuration, dataContext);
        var next = CreateNodeDelegate();

        return (dataContext, nodeContext, next);
    }
}
