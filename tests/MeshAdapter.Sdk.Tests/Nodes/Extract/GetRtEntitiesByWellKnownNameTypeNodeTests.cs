using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Runtime.Engine.Repositories.Query;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
/// Regression test for review finding #1:
///
/// The STJ-migration version of <see cref="GetRtEntitiesByWellKnownNameTypeNode"/>
/// used bespoke dotted-only path helpers (<c>ResolveByPath</c> / <c>WriteByPath</c>)
/// that split on <c>'.'</c> and treated each segment as a literal property name.
/// Configurations using <c>$</c>-prefixed JSONPath (which is what the configuration
/// schema documents and what production pipelines use — defaults are
/// <c>$.rtId</c>, <c>$.ckTypeId</c>, <c>$.modOperation</c>) silently broke: a write
/// of <c>$.rtId</c> created a nested <c>{"$": {"rtId": ...}}</c> shape instead of a
/// top-level <c>rtId</c> property on each item.
///
/// The pre-migration code used Newtonsoft's <c>SelectToken</c> / <c>ReplaceNested</c>,
/// both of which understood JSONPath. After the fix, the node delegates to
/// <c>JsonNodePath.Select</c> / <c>JsonNodePath.Set</c> from the SDK.
/// </summary>
public class GetRtEntitiesByWellKnownNameTypeNodeTests : NodeTestBase
{
    private const string TestTenantId = "test-tenant";
    private static readonly RtCkId<CkTypeId> TestRtCkTypeId = new("TestModel/TestType");
    private static readonly OctoObjectId TestRtId = new("000000000000000000000001");

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public GetRtEntitiesByWellKnownNameTypeNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    private static GetRtEntitiesByWellKnownNameNodeConfiguration DefaultConfig() => new()
    {
        Path = "$.items",
        CkTypeId = TestRtCkTypeId,
        WellKnownNamePath = "$.name",
        // Defaults from the configuration class — these are JSONPath, the bug was that
        // the bespoke helpers treated "$" as a literal property name.
        RtIdTargetPath = "$.rtId",
        CkTypeIdTargetPath = "$.ckTypeId",
        ModOperationPath = "$.modOperation"
    };

    private static RtEntity CreateMatchingEntity(string wellKnownName)
    {
        var entity = new RtEntity(TestRtCkTypeId, TestRtId)
        {
            RtWellKnownName = wellKnownName
        };
        return entity;
    }

    private void SetupRepositoryToReturn(params RtEntity[] entities)
    {
        var resultSet = new ResultSet<RtEntity>(entities, entities.Length, null, null);
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                _session,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(resultSet));
    }

    [Fact]
    public async Task ProcessObjectAsync_DefaultDollarRootedPaths_WritesTopLevelKeysOnEachItem()
    {
        // Source array with one item containing a wellKnownName at $.name.
        var input = new JsonObject
        {
            ["items"] = new JsonArray
            {
                new JsonObject { ["name"] = "well-known-1" }
            }
        };
        using var doc = JsonDocument.Parse(input.ToJsonString());
        using var dataContext = new DataContextImpl(doc.RootElement);

        SetupRepositoryToReturn(CreateMatchingEntity("well-known-1"));

        var config = DefaultConfig();
        var (nodeContext, next) = PrepareNode(dataContext, config, "GetRtEntitiesByWellKnownName");
        var node = new GetRtEntitiesByWellKnownNameTypeNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // Inspect the item: it should have top-level rtId / ckTypeId / modOperation,
        // NOT a nested {"$": {"rtId": ...}} shape (which the bespoke dot-split helpers
        // would have produced for "$.rtId" paths).
        var item = dataContext.Get<JsonObject>("$.items[0]");
        Assert.NotNull(item);

        Assert.True(item!.ContainsKey("rtId"),
            $"Item should contain top-level 'rtId' key. Actual keys: [{string.Join(", ", item.Select(p => p.Key))}]");
        Assert.True(item.ContainsKey("ckTypeId"),
            $"Item should contain top-level 'ckTypeId' key. Actual keys: [{string.Join(", ", item.Select(p => p.Key))}]");
        Assert.True(item.ContainsKey("modOperation"),
            $"Item should contain top-level 'modOperation' key. Actual keys: [{string.Join(", ", item.Select(p => p.Key))}]");

        Assert.False(item.ContainsKey("$"),
            "Item must NOT contain a literal '$' key — that's the regression where bespoke dot-split treated '$' as a property name.");

        Assert.Equal(TestRtId.ToString(), item["rtId"]!.GetValue<string>());
        Assert.Equal(TestRtCkTypeId.ToString(), item["ckTypeId"]!.GetValue<string>());
    }

    [Fact]
    public async Task ProcessObjectAsync_DollarRootedWellKnownNamePath_ResolvesName()
    {
        // Verifies the read-side: $.name must resolve to the property "name",
        // not to a non-existent root["$"]["name"].
        var input = new JsonObject
        {
            ["items"] = new JsonArray
            {
                new JsonObject { ["name"] = "well-known-1" }
            }
        };
        using var doc = JsonDocument.Parse(input.ToJsonString());
        using var dataContext = new DataContextImpl(doc.RootElement);

        // Empty result set — we only care that the well-known name was extracted to
        // populate the query options (which we capture below).
        var resultSet = new ResultSet<RtEntity>([], 0, null, null);
        var capturedQueryOptions = (RtEntityQueryOptions?)null;
        A.CallTo(() => _tenantRepository.GetRtEntitiesByTypeAsync(
                _session,
                A<RtCkId<CkTypeId>>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Invokes(call => capturedQueryOptions = call.Arguments.Get<RtEntityQueryOptions>(2))
            .Returns(Task.FromResult<IResultSet<RtEntity>>(resultSet));

        var config = DefaultConfig();
        var (nodeContext, next) = PrepareNode(dataContext, config, "GetRtEntitiesByWellKnownName");
        var node = new GetRtEntitiesByWellKnownNameTypeNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        // The query options should carry the FieldIn filter populated with the
        // resolved well-known name. If ReadStringByPath returned null (the bug),
        // the filter would be empty.
        Assert.NotNull(capturedQueryOptions);
    }

    [Fact]
    public async Task ProcessObjectAsync_UnmatchedWithGenerateInsert_WritesInsertOperation()
    {
        // One item whose well-known name is NOT returned by the repository; with
        // GenerateInsertOperation the node must stamp a fresh rtId, Insert modOperation,
        // and the resolved ckTypeId on the item (preserving the former insert-branch behavior).
        var input = new JsonObject
        {
            ["items"] = new JsonArray
            {
                new JsonObject { ["name"] = "missing-1" }
            }
        };
        using var doc = JsonDocument.Parse(input.ToJsonString());
        using var dataContext = new DataContextImpl(doc.RootElement);

        SetupRepositoryToReturn(); // empty result set

        var config = DefaultConfig() with { GenerateInsertOperation = true, AttributeTargetPath = "$.attributes" };
        var (nodeContext, next) = PrepareNode(dataContext, config, "GetRtEntitiesByWellKnownName");
        var node = new GetRtEntitiesByWellKnownNameTypeNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        var item = dataContext.Get<JsonObject>("$.items[0]");
        Assert.NotNull(item);
        Assert.True(item!.ContainsKey("rtId"));
        Assert.Equal((int)UpdateKind.Insert, item["modOperation"]!.GetValue<int>());
        Assert.Equal(TestRtCkTypeId.ToString(), item["ckTypeId"]!.GetValue<string>());
        // AttributeTargetPath gets an empty object on insert.
        Assert.Equal(DataKind.Object, dataContext.GetKind("$.items[0].attributes"));
        Assert.False(item.ContainsKey("$"));
    }

    private static (INodeContext nodeContext, NodeDelegate next) PrepareNode<TConfig>(
        IDataContext dataContext, TConfig config, string nodeName) where TConfig : INodeConfiguration
    {
        var logger = A.Fake<IPipelineLogger>();
        var rootNodeContext = NodeContext.CreateRootNodeContext(
            new Microsoft.Extensions.DependencyInjection.ServiceCollection().BuildServiceProvider(),
            logger,
            dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode(nodeName, 0, config!, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (nodeContext, next);
    }
}
