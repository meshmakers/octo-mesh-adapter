using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using MeshAdapter.Sdk.IntegrationTests.Nodes.Notifications;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Extract;

/// <summary>
///     AB#8355 — against a real MongoDB: a persisted query deleted with the default strategy stays in its collection
///     with <c>rtState = Archived</c>, and <c>GetQueryById@1</c> reports it as not found instead of executing it.
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class GetQueryByIdDeletedQueryIntegrationTests(NotificationEngineFixture fixture)
    : IClassFixture<NotificationEngineFixture>
{
    private NotificationEngineHarness NewScenario()
    {
        fixture.EnsureInitialized();
        return new NotificationEngineHarness(fixture);
    }

    [Fact]
    public async Task ADeletedQuery_IsQueryNotFound_AndIsNotExecuted()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);

        var (before, beforeFailure) = await RunGetQueryByIdAsync(h);
        beforeFailure.Should().BeNull();
        before.Should().Be(2, "positive control: the stored query runs before it is deleted");

        await h.DeleteQueryAsync();

        // The premise of the bug: the plain lookup by id still returns the deleted query.
        var stored = await h.ReadQueryIncludingArchivedAsync();
        stored.Should().NotBeNull();
        stored!.RtState.Should().Be(RtState.Archived);

        var (after, failure) = await RunGetQueryByIdAsync(h);

        after.Should().BeNull("a deleted query must not emit a result");
        failure.Should().BeOfType<MeshAdapterPipelineExecutionException>()
            .Which.Message.Should().Contain($"Query with RtId '{h.QueryRtId}' not found");
    }

    private async Task<(int? RowCount, Exception? Failure)> RunGetQueryByIdAsync(NotificationEngineHarness h)
    {
        var config = new GetQueryByIdNodeConfiguration { QueryRtId = h.QueryRtId, TargetPath = "$.queryResult" };
        using var document = JsonDocument.Parse("{}");
        using var dataContext = new DataContextImpl(document.RootElement);
        var rootContext = NodeContext.CreateRootNodeContext(fixture.Provider!, A.Fake<IPipelineLogger>(), dataContext);
        var nodeContext = rootContext.RegisterChildNode("GetQueryById", 0, config, dataContext);

        int? result = null;
        var node = new GetQueryByIdNode((dc, _) =>
        {
            var emitted = dc.Get<JsonNode>(config.TargetPath)!.AsObject();
            var rows = emitted.FirstOrDefault(p => string.Equals(p.Key, "rows", StringComparison.OrdinalIgnoreCase));
            result = rows.Value?.AsArray().Count;
            return Task.CompletedTask;
        }, h.CreateEtlContext(), fixture.GetService<ICkCacheService>(), fixture.GetSystemContext());
        try
        {
            await node.ProcessObjectAsync(dataContext, nodeContext);
            return (result, null);
        }
        catch (Exception ex)
        {
            return (result, ex);
        }
    }
}
