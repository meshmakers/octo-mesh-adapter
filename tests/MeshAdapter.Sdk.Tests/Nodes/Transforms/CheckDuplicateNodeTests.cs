using System.Text.Json;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.Repositories.Query;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Transforms;

/// <summary>
/// Regression test for the dedup-key strict-read finding:
///
/// Pre-migration the dedup value was read with <c>SelectToken(path)?.ToString()</c>, which
/// stringified ANY token (<c>42</c> → <c>"42"</c>). The STJ port reads with
/// <c>Get&lt;string&gt;(path)</c>, which throws <see cref="InvalidOperationException"/> on a
/// numeric/object value path — so a numeric dedup key crashes the node. Same root cause as the
/// Excel-import strict reads.
/// </summary>
public class CheckDuplicateNodeTests
{
    [Fact]
    public async Task ProcessObjectAsync_NumericValuePath_CoercesToStringFilter()
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        var tenantRepository = A.Fake<ITenantRepository>();
        var session = A.Fake<IOctoSession>();
        A.CallTo(() => etlContext.TenantRepository).Returns(tenantRepository);
        A.CallTo(() => tenantRepository.GetSessionAsync()).Returns(Task.FromResult(session));

        var resultSet = A.Fake<IResultSet<RtEntity>>();
        A.CallTo(() => resultSet.TotalCount).Returns(0);
        A.CallTo(() => resultSet.Items).Returns(new List<RtEntity>());

        RtEntityQueryOptions? captured = null;
        A.CallTo(() => tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .Invokes(call => captured = call.GetArgument<RtEntityQueryOptions>(2))
            .Returns(resultSet);

        var config = new CheckDuplicateNodeConfiguration
        {
            CkTypeId = new RtCkId<CkTypeId>("TestModel/TestType"),
            AttributeName = "DeviceId",
            ValuePath = "$.deviceId",
            TargetPath = "$.isDuplicate"
        };

        var dataContext = new DataContextImpl(JsonDocument.Parse("""{ "deviceId": 42 }"""));
        var logger = A.Fake<IPipelineLogger>();
        var rootNodeContext = NodeContext.CreateRootNodeContext(
            new ServiceCollection().BuildServiceProvider(), logger, dataContext);
        var nodeContext = rootNodeContext.RegisterChildNode("CheckDuplicate", 0, config, dataContext);

        var next = A.Fake<NodeDelegate>();
        var node = new CheckDuplicateNode(next, etlContext);

        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => tenantRepository.GetRtEntitiesByTypeAsync(
                A<IOctoSession>._, A<RtCkId<CkTypeId>>._, A<RtEntityQueryOptions>._, A<int?>._, A<int?>._))
            .MustHaveHappenedOnceExactly();
        Assert.NotNull(captured);
        var filter = Assert.Single(captured!.FieldFilters!);
        Assert.Equal("42", filter.ComparisonValue);
    }
}
