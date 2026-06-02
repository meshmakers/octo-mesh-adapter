using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.Tests.Helpers;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Extract;

/// <summary>
/// Regression test for review finding #6:
///
/// <see cref="GetAssociationTargetsNode"/>'s <c>GetOriginRtIds</c> helper resolved
/// <see cref="GetAssociationTargetsNodeConfiguration.OriginRtIdPath"/> via
/// <c>GetKind(path)</c> + <c>Get&lt;OctoObjectId?&gt;(path)</c> for non-array kinds.
/// For wildcard JSONPath expressions (e.g. <c>$.items[*].rtId</c>), <c>GetKind</c>
/// returned the kind of the first match (typically String) and the subsequent
/// <c>Get&lt;OctoObjectId?&gt;</c> returned only that first ID — silently collapsing
/// N-element wildcard expansions to a single origin ID. Same root cause as
/// finding #5; uses the same <c>EnumerateMatches</c>-based fix.
/// </summary>
public class GetAssociationTargetsNodeTests : NodeTestBase
{
    private const string TestTenantId = "test-tenant";
    private static readonly RtCkId<CkTypeId> TestOriginCkTypeId = new("TestModel/OriginType");
    private static readonly RtCkId<CkTypeId> TestTargetCkTypeId = new("TestModel/TargetType");
    private static readonly RtCkId<CkAssociationRoleId> TestAssociationRoleId = new("TestModel/TestRole");

    private readonly IMeshEtlContext _etlContext;
    private readonly ITenantRepository _tenantRepository;
    private readonly IOctoSession _session;

    public GetAssociationTargetsNodeTests()
    {
        _etlContext = A.Fake<IMeshEtlContext>();
        _tenantRepository = A.Fake<ITenantRepository>();
        _session = A.Fake<IOctoSession>();

        A.CallTo(() => _etlContext.TenantId).Returns(TestTenantId);
        A.CallTo(() => _etlContext.TenantRepository).Returns(_tenantRepository);
        A.CallTo(() => _tenantRepository.GetSessionAsync()).Returns(Task.FromResult(_session));
    }

    [Fact]
    public async Task ProcessObjectAsync_WildcardOriginRtIdPath_ExpandsToAllMatchingIds()
    {
        const string json = """
            {
                "items": [
                    { "rtId": "000000000000000000000001" },
                    { "rtId": "000000000000000000000002" },
                    { "rtId": "000000000000000000000003" }
                ]
            }
            """;
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        IEnumerable<OctoObjectId>? capturedOriginRtIds = null;
        var emptyResult = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => emptyResult.Count).Returns(0);
        A.CallTo(() => emptyResult.GetEnumerator()).Returns(
            new List<KeyValuePair<RtEntityId, IResultSet<RtEntity>>>().GetEnumerator());

        A.CallTo(() => _tenantRepository.GetRtAssociationTargetsAsync(
                _session,
                A<IEnumerable<OctoObjectId>>._,
                A<RtCkId<CkTypeId>>._,
                A<RtCkId<CkAssociationRoleId>>._,
                A<RtCkId<CkTypeId>>._,
                A<GraphDirections>._,
                A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._,
                A<int?>._,
                A<int?>._))
            .Invokes(call => capturedOriginRtIds = call.Arguments.Get<IEnumerable<OctoObjectId>>(1))
            .Returns(Task.FromResult(emptyResult));

        var config = new GetAssociationTargetsNodeConfiguration
        {
            OriginRtIdPath = "$.items[*].rtId",
            OriginCkTypeId = TestOriginCkTypeId,
            TargetCkTypeId = TestTargetCkTypeId,
            AssociationRoleId = TestAssociationRoleId,
            GraphDirection = Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects.GraphDirectionsDto.Outbound,
            TargetPath = "$.result"
        };
        var (nodeContext, next) = PrepareNode(dataContext, config, "GetAssociationTargets");
        var node = new GetAssociationTargetsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedOriginRtIds);
        var ids = capturedOriginRtIds!.ToList();
        // Bug behavior: only the first match's rtId is passed.
        // Fixed behavior: all three matches are expanded.
        Assert.Equal(3, ids.Count);
        Assert.Contains(new OctoObjectId("000000000000000000000001"), ids);
        Assert.Contains(new OctoObjectId("000000000000000000000002"), ids);
        Assert.Contains(new OctoObjectId("000000000000000000000003"), ids);
    }

    [Fact]
    public async Task ProcessObjectAsync_ScalarOriginRtIdPath_ProducesSingleId()
    {
        // Sanity: non-wildcard scalar path still produces exactly one origin id.
        const string json = """{ "rtId": "000000000000000000000042" }""";
        using var doc = JsonDocument.Parse(json);
        using var dataContext = new DataContextImpl(doc.RootElement);

        IEnumerable<OctoObjectId>? capturedOriginRtIds = null;
        var emptyResult = A.Fake<IMultipleOriginResultSet<RtEntity>>();
        A.CallTo(() => emptyResult.GetEnumerator()).Returns(
            new List<KeyValuePair<RtEntityId, IResultSet<RtEntity>>>().GetEnumerator());

        A.CallTo(() => _tenantRepository.GetRtAssociationTargetsAsync(
                _session, A<IEnumerable<OctoObjectId>>._, A<RtCkId<CkTypeId>>._,
                A<RtCkId<CkAssociationRoleId>>._, A<RtCkId<CkTypeId>>._,
                A<GraphDirections>._, A<IReadOnlyList<OctoObjectId>?>._,
                A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Invokes(call => capturedOriginRtIds = call.Arguments.Get<IEnumerable<OctoObjectId>>(1))
            .Returns(Task.FromResult(emptyResult));

        var config = new GetAssociationTargetsNodeConfiguration
        {
            OriginRtIdPath = "$.rtId",
            OriginCkTypeId = TestOriginCkTypeId,
            TargetCkTypeId = TestTargetCkTypeId,
            AssociationRoleId = TestAssociationRoleId,
            GraphDirection = Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects.GraphDirectionsDto.Outbound,
            TargetPath = "$.result"
        };
        var (nodeContext, next) = PrepareNode(dataContext, config, "GetAssociationTargets");
        var node = new GetAssociationTargetsNode(next, _etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        Assert.NotNull(capturedOriginRtIds);
        var ids = capturedOriginRtIds!.ToList();
        Assert.Single(ids);
        Assert.Equal(new OctoObjectId("000000000000000000000042"), ids[0]);
    }

    private static (INodeContext nodeContext, NodeDelegate next) PrepareNode<TConfig>(
        IDataContext dataContext, TConfig config, string nodeName) where TConfig : INodeConfiguration
    {
        var logger = A.Fake<IPipelineLogger>();
        var rootNodeContext = NodeContext.CreateRootNodeContext(
            new ServiceCollection().BuildServiceProvider(),
            logger,
            dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode(nodeName, 0, config!, dataContext);
        var next = A.Fake<NodeDelegate>();
        return (nodeContext, next);
    }
}
