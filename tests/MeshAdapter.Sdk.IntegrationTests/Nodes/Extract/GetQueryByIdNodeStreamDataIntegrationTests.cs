using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
/// Integration tests for GetQueryByIdNode executing a persisted simple stream-data query
/// (RtSimpleSdQuery) end-to-end against a real CrateDB archive seeded by <see cref="StreamDataFixture"/>.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class GetQueryByIdNodeStreamDataIntegrationTests(StreamDataFixture fixture)
    : IClassFixture<StreamDataFixture>
{
    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_ReturnsTimeSeriesWithMappedColumns()
    {
        // Arrange
        fixture.EnsureInitialized();

        var queryRtId = await CreateSimpleStreamDataQueryAsync(
            "SimpleSdQuery_HappyPath",
            "Temperature", "Amount.Value", "Amount.Unit");

        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryRtId,
            TargetPath = "$.queryResult"
        };

        // Act
        var result = await ExecuteNodeAndGetQueryResultAsync(config);

        // Assert
        result.Should().NotBeNull();

        // Leading Timestamp column followed by the projected attribute paths (headers keep the
        // user's dotted / mixed-case form).
        result!.Columns.Select(c => c.Header)
            .Should().ContainInOrder("Timestamp", "Temperature", "Amount.Value", "Amount.Unit");

        result.Rows.Should().HaveCount(fixture.TestDataPointCount);

        // Every projected value must be non-null — the physical-column-name mapping resolves the
        // dotted paths (Amount.Value -> amountvalue) that were previously returned as null.
        foreach (var row in result.Rows)
        {
            row.Values[0].Should().NotBeNull("Timestamp must be populated");
            row.Values[1].Should().NotBeNull("Temperature must be populated");
            row.Values[2].Should().NotBeNull("Amount.Value must resolve via physical column name");
            row.Values[3].Should().NotBeNull("Amount.Unit must resolve via physical column name");
        }

        // The unit is a constant across the seeded points.
        result.Rows.Select(r => r.Values[3]?.ToString()).Should().AllBe("kWh");
    }

    [Fact]
    public async Task ProcessObjectAsync_WithSimpleStreamDataQuery_AppliesLimitOverride()
    {
        // Arrange
        fixture.EnsureInitialized();

        var queryRtId = await CreateSimpleStreamDataQueryAsync(
            "SimpleSdQuery_Limit", "Temperature");

        var config = new GetQueryByIdNodeConfiguration
        {
            QueryRtId = queryRtId,
            TargetPath = "$.queryResult",
            Limit = 2
        };

        // Act
        var result = await ExecuteNodeAndGetQueryResultAsync(config);

        // Assert
        result.Should().NotBeNull();
        result!.Rows.Should().HaveCount(2, "Limit=2 caps the returned rows");
    }

    private async Task<OctoObjectId> CreateSimpleStreamDataQueryAsync(string name, params string[] columns)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        await tenantRepository.LoadCacheForTenantAsync(ckCacheService);

        using var session = await tenantRepository.GetSessionAsync();
        session.StartTransaction();

        var query = await tenantRepository.CreateTransientRtEntityAsync<RtSimpleSdQuery>();
        query.Name = name;
        query.RtWellKnownName = name;
        query.QueryCkTypeId = fixture.TestCkTypeId;
        query.ArchiveRtId = fixture.ArchiveRtIdString;
        query.Columns = new AttributeStringValueList(columns.ToList());

        await tenantRepository.InsertOneRtEntityAsync(session, query);
        await session.CommitTransactionAsync();

        return query.RtId;
    }

    private async Task<QueryResult?> ExecuteNodeAndGetQueryResultAsync(
        GetQueryByIdNodeConfiguration config)
    {
        var systemContext = fixture.GetSystemContext();
        var tenantRepository = systemContext.GetSystemTenantRepository();
        var ckCacheService = fixture.GetService<ICkCacheService>();
        var meshEtlContext = CreateMeshEtlContext(tenantRepository);

        QueryResult? capturedResult = null;
        var dataContext = A.Fake<IDataContext>();
        A.CallTo(dataContext)
            .Where(call => call.Method.Name == nameof(IDataContext.Set))
            .Invokes(call =>
            {
                if (call.Arguments[1] is QueryResult qr)
                {
                    capturedResult = qr;
                }
            });

        var logger = A.Fake<IPipelineLogger>();
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, logger, dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetQueryById", 0, config, dataContext);

        Task Next(IDataContext dc, INodeContext nc) => Task.CompletedTask;
        var node = new GetQueryByIdNode(Next, meshEtlContext, ckCacheService, systemContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        return capturedResult;
    }

    private static MeshEtlContext CreateMeshEtlContext(ITenantRepository tenantRepository)
    {
        var pipelineId = new OctoObjectId("000000000000000000000099");

        var globalConfig = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => globalConfig.GetNames()).Returns(Enumerable.Empty<string>());
        A.CallTo(() => globalConfig.IsDefined(A<string>._)).Returns(false);

        return new MeshEtlContext(
            tenantId: tenantRepository.TenantId,
            tenantRepository: tenantRepository,
            dataFlowRtId: pipelineId,
            pipelineExecutionId: Guid.NewGuid(),
            pipelineRtEntityId: new RtEntityId("System/RtDataPipeline", pipelineId),
            adapterReceivedDateTime: DateTime.UtcNow,
            externalReceivedDateTime: null,
            globalConfiguration: globalConfig,
            properties: new Dictionary<string, object?>());
    }
}
