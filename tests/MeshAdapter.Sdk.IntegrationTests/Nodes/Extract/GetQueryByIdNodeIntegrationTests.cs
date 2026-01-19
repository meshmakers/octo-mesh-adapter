using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Newtonsoft.Json.Linq;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
/// Integration tests for GetQueryByIdNode.
/// These tests run against a real MongoDB instance (Test container)
/// with the System CK model imported.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class GetQueryByIdNodeIntegrationTests(SampleDataFixture fixture) : IClassFixture<SampleDataFixture>
{
    [Fact]
    public async Task ProcessObjectAsync_WithNonExistentQuery_DoesNotCallNext()
    {
        // Arrange
        fixture.EnsureInitialized();

        var queryId = new OctoObjectId("000000000000000000000001");
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryId,
            TargetPath = "$.queryResult"
        };

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        var meshEtlContext = CreateMeshEtlContext(tenantRepository);
        var (dataContext, nodeContext, _) = CreateNodeTestContext(config);

        bool nextCalled = false;

        Task TrackingNext(IDataContext dataContext1, INodeContext nodeContext1)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }

        var node = new GetQueryByIdNode(TrackingNext, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert - next should NOT be called when query is not found
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithNonExistentQuery_DoesNotSetResult()
    {
        // Arrange
        fixture.EnsureInitialized();

        var queryId = new OctoObjectId("000000000000000000000002");
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryId,
            TargetPath = "$.queryResult"
        };

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        var meshEtlContext = CreateMeshEtlContext(tenantRepository);
        var (dataContext, nodeContext, next) = CreateNodeTestContext(config);

        var node = new GetQueryByIdNode(next, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert - result should not be set when query is not found
        var result = dataContext.Current?["queryResult"];
        result.Should().BeNull();
    }

    [Fact]
    public Task GetQueryByIdNode_CanBeInstantiatedWithDependencies()
    {
        // Arrange
        fixture.EnsureInitialized();

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        Task Next(IDataContext dataContext, INodeContext nodeContext) => Task.CompletedTask;

        // Act & Assert - should not throw
        var node = new GetQueryByIdNode(Next, meshEtlContext, ckCacheService);
        node.Should().NotBeNull();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task ProcessObjectAsync_WithEmptyQueryId_DoesNotCallNext()
    {
        // Arrange
        fixture.EnsureInitialized();

        // Empty/default ObjectId
        var queryId = new OctoObjectId("000000000000000000000000");
        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryId,
            TargetPath = "$.queryResult"
        };

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        var meshEtlContext = CreateMeshEtlContext(tenantRepository);
        var (dataContext, nodeContext, _) = CreateNodeTestContext(config);

        bool nextCalled = false;

        Task TrackingNext(IDataContext dataContext1, INodeContext nodeContext1)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }

        var node = new GetQueryByIdNode(TrackingNext, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert - next should NOT be called when query is not found
        nextCalled.Should().BeFalse();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithExistingQuery_CallsNext()
    {
        // Arrange
        fixture.EnsureInitialized();

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        // Create a query entity in the database
        var queryId = await CreateQueryEntityAsync(tenantRepository, "TestQueryForCallsNext");

        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryId,
            TargetPath = "$.queryResult"
        };

        var meshEtlContext = CreateMeshEtlContext(tenantRepository);
        var (dataContext, nodeContext, _) = CreateNodeTestContext(config);

        bool nextCalled = false;

        Task TrackingNext(IDataContext dataContext1, INodeContext nodeContext1)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }

        var node = new GetQueryByIdNode(TrackingNext, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert - next should be called when query is found
        nextCalled.Should().BeTrue();
    }

    [Fact]
    public async Task ProcessObjectAsync_WithExistingQuery_SetsQueryResult()
    {
        // Arrange
        fixture.EnsureInitialized();

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        // Create a query entity in the database
        var queryId = await CreateQueryEntityAsync(tenantRepository, "TestQueryForResult");

        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryId,
            TargetPath = "$.queryResult"
        };

        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        // Create dataContext with tracking
        var dataContext = A.Fake<IDataContext>();
        var currentData = new JObject();
        bool setValueCalled = false;
        object? capturedValue = null;

        A.CallTo(() => dataContext.Current).Returns(currentData);
        // Configure the generic SetValueByPath method using AnyCall matching
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.SetValueByPath))
            .Invokes(call =>
            {
                setValueCalled = true;
                var path = call.Arguments[0] as string;
                var value = call.Arguments[4]; // T? value is the 5th argument
                capturedValue = value;
                if (!string.IsNullOrEmpty(path))
                {
                    var key = path.TrimStart('$', '.');
                    currentData[key] = value != null ? JToken.FromObject(value) : JValue.CreateNull();
                }
            });

        var logger = A.Fake<IPipelineLogger>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, logger, dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetQueryById", 0, config, dataContext);

        bool nextCalled = false;

        Task TrackingNext(IDataContext dataContext1, INodeContext nodeContext1)
        {
            nextCalled = true;
            return Task.CompletedTask;
        }

        var node = new GetQueryByIdNode(TrackingNext, meshEtlContext, ckCacheService);

        // Act
        Exception? caughtException = null;
        try
        {
            await node.ProcessObjectAsync(dataContext, nodeContext);
        }
        catch (Exception ex)
        {
            caughtException = ex;
        }

        // Assert - no exception should be thrown
        caughtException.Should().BeNull($"ProcessObjectAsync should not throw, but threw: {caughtException?.Message}");

        // Assert - next should be called when query is found
        nextCalled.Should().BeTrue("next should be called when query is found");

        // Assert - SetValueByPath should have been called
        setValueCalled.Should().BeTrue("SetValueByPath should be called when query is found");
        capturedValue.Should().NotBeNull("The captured value should not be null");

        // Assert - result should be set when query is found
        var result = dataContext.Current?["queryResult"];
        result.Should().NotBeNull();

        // Assert - validate the QueryResult structure and content
        // The QueryResult is internal but accessible via InternalsVisibleTo
        var queryResult = capturedValue as Meshmakers.Octo.Sdk.MeshAdapter.Nodes.QueryResult;
        queryResult.Should().NotBeNull("capturedValue should be a QueryResult");

        // Verify columns match what we defined in CreateQueryEntityAsync
        queryResult!.Columns.Should().HaveCount(3, "we defined 3 columns: RtId, CkTypeId, RtWellKnownName");
        queryResult.Columns.Select(c => c.Header).Should().ContainInOrder("RtId", "CkTypeId", "RtWellKnownName");

        // Verify that the query returned results (at least the query entity itself since we query for RtSimpleRtQuery)
        queryResult.Rows.Should().NotBeEmpty("the query should return at least one result");
    }

    [Fact]
    public async Task ProcessObjectAsync_WithExistingQuery_SetsResultAtTargetPath()
    {
        // Arrange
        fixture.EnsureInitialized();

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();

        // Create a query entity in the database
        var queryId = await CreateQueryEntityAsync(tenantRepository, "TestQueryForResultType");

        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryId,
            TargetPath = "$.queryResult"
        };

        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        // Capture what was set at the target path
        object? capturedValue = null;
        var dataContext = A.Fake<IDataContext>();
        var currentData = new JObject();
        A.CallTo(() => dataContext.Current).Returns(currentData);
        // Configure the generic SetValueByPath method using AnyCall matching
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.SetValueByPath))
            .Invokes(call =>
            {
                var path = call.Arguments[0] as string;
                var value = call.Arguments[4]; // T? value is the 5th argument
                if (path == "$.queryResult")
                {
                    capturedValue = value;
                }
            });

        var logger = A.Fake<IPipelineLogger>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, logger, dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetQueryById", 0, config, dataContext);

        Task Next(IDataContext dataContext1, INodeContext nodeContext1) => Task.CompletedTask;
        var node = new GetQueryByIdNode(Next, meshEtlContext, ckCacheService);

        // Act
        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Assert - a value should be set at the target path
        capturedValue.Should().NotBeNull();
    }

    [Fact]
    public async Task TenantRepository_CanGetSession()
    {
        // Arrange
        fixture.EnsureInitialized();

        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();

        // Act
        using var session = await tenantRepository.GetSessionAsync();

        // Assert
        session.Should().NotBeNull();
    }

    #region Helper Methods

    /// <summary>
    /// Creates an RtSimpleRtQuery entity in the database for testing.
    /// </summary>
    private async Task<OctoObjectId> CreateQueryEntityAsync(ITenantRepository tenantRepository, string queryName)
    {
        // Load the CK cache for the tenant before creating entities
        var ckCacheService = fixture.GetService<ICkCacheService>();
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        // Create a transient RtSimpleRtQuery entity using typed approach
        var rtQuery = await tenantRepository.CreateTransientRtEntityAsync<RtSimpleRtQuery>();
        rtQuery.RtWellKnownName = queryName;
        rtQuery.Name = queryName; // Mandatory attribute
        rtQuery.QueryCkTypeId = rtQuery.CkTypeId?.ToString()!; // Query for queries themselves
        rtQuery.Columns = new AttributeStringValueList(["RtId", "CkTypeId", "RtWellKnownName"]);

        await tenantRepository.InsertOneRtEntityAsync(session, rtQuery);
        await session.CommitTransactionAsync();

        return rtQuery.RtId;
    }

    private MeshEtlContext CreateMeshEtlContext(ITenantRepository tenantRepository)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");
        var executionId = Guid.NewGuid();

        // Use FakeItEasy for IGlobalConfiguration
        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);

        return new MeshEtlContext(
            tenantId: tenantRepository.TenantId,
            tenantRepository: tenantRepository,
            dataPipelineRtId: pipelineId,
            pipelineExecutionId: executionId,
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfig,
            properties: new Dictionary<string, object?>()
        );
    }

    private (IDataContext dataContext, INodeContext nodeContext, NodeDelegate next) CreateNodeTestContext<TConfig>(
        TConfig config) where TConfig : class, INodeConfiguration
    {
        // Create a fake data context using FakeItEasy
        var dataContext = A.Fake<IDataContext>();
        var currentData = new JObject();

        A.CallTo(() => dataContext.Current).Returns(currentData);
        // Configure the generic SetValueByPath method using AnyCall matching
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.SetValueByPath))
            .Invokes(call =>
            {
                var path = call.Arguments[0] as string;
                var value = call.Arguments[4]; // T? value is the 5th argument
                if (!string.IsNullOrEmpty(path))
                {
                    var key = path.TrimStart('$', '.');
                    currentData[key] = value != null ? JToken.FromObject(value) : JValue.CreateNull();
                }
            });

        // Create pipeline logger fake
        var logger = A.Fake<IPipelineLogger>();

        var rootContext = NodeContext.CreateRootNodeContext(
            fixture.Provider!,
            logger,
            dataContext);

        var nodeContext = rootContext.RegisterChildNode(
            typeof(TConfig).Name.Replace("Configuration", ""),
            0,
            config,
            dataContext);

        Task Next(IDataContext dataContext1, INodeContext nodeContext1) => Task.CompletedTask;

        return (dataContext, nodeContext, Next);
    }

    #endregion
}
