using System.Text.Json;
using System.Text.Json.Nodes;
using FakeItEasy;
using MeshAdapter.Sdk.IntegrationTests.Fixtures;
using MeshAdapter.Sdk.IntegrationTests.Nodes.Notifications;
using Meshmakers.Octo.ConstructionKit.Contracts.Services;
using Meshmakers.Octo.ConstructionKit.Models.System.Generated.System.v2;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Common;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Runtime.Contracts;

namespace MeshAdapter.Sdk.IntegrationTests.Nodes.Access;

/// <summary>
///     AB#8049 — a stored query (<c>System/SimpleRtQuery</c>) whose own field filter or sort order reaches a Hidden
///     attribute is refused by every node that runs stored queries, against a real MongoDB: <c>GetQueryById@1</c> and
///     the notification engine (<c>PrepareNotification@1</c>). Without the check the number of returned rows (or
///     whether a notification is sent) answers a guess of the hidden value. Test attribute:
///     <c>MeshAdapterIntegrationTest/SensorReading.ReadingSecret</c> (<c>access: Hidden</c>).
/// </summary>
[Trait("Category", "Integration")]
[Collection("Sequential")]
public class StoredQueryHiddenAttributeIntegrationTests(NotificationEngineFixture fixture)
    : IClassFixture<NotificationEngineFixture>
{
    private const string HiddenPath = "ReadingSecret";

    private NotificationEngineHarness NewScenario()
    {
        fixture.EnsureInitialized();
        return new NotificationEngineHarness(fixture);
    }

    [Fact]
    public async Task GetQueryById_AStoredFilterOnAHiddenAttribute_IsRefused_BeforeTheQueryRuns()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        await h.FilterQueryAsync(HiddenPath);

        var (result, failure) = await RunGetQueryByIdAsync(h);

        result.Should().BeNull("the node must not emit a result");
        var hidden = failure.Should().BeOfType<RtAttributeHiddenException>().Subject;
        hidden.Code.Should().Be(RtAttributeAccessCodes.HiddenAttribute);
        hidden.AttributePath.Should().Be(HiddenPath);
        hidden.Usage.Should().Be("stored field filter");
    }

    [Fact]
    public async Task GetQueryById_AStoredSortOnAHiddenAttribute_IsRefused()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.SortQueryAsync(HiddenPath, RtSortOrdersEnum.Ascending);

        var (_, failure) = await RunGetQueryByIdAsync(h);

        failure.Should().BeOfType<RtAttributeHiddenException>().Which.Usage.Should().Be("stored sort order");
    }

    [Fact]
    public async Task GetQueryById_AStoredQueryOnVisibleAttributes_StillRuns()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.AddReadingsAsync(2);
        await h.SortQueryAsync("SerialNumber", RtSortOrdersEnum.Ascending);

        var (result, failure) = await RunGetQueryByIdAsync(h);

        failure.Should().BeNull();
        result.Should().Be(2, "positive control: the stored query is executed");
    }

    [Fact]
    public async Task PrepareNotification_AStoredFilterOnAHiddenAttribute_IsRefusedWithAReason_AndNothingIsSent()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.FilterQueryAsync(HiddenPath);
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Sent.Should().BeFalse();
        tick.Failure.Should().BeOfType<RtAttributeHiddenException>();
        (await h.StateOfAsync(preference))!.LastError.Should().Contain(RtAttributeAccessCodes.HiddenAttribute)
            .And.Contain(HiddenPath);
    }

    [Fact]
    public async Task PrepareNotification_AStoredSortOnAHiddenAttribute_IsRefused()
    {
        var h = NewScenario();
        await h.CreateSubscriptionAsync();
        await h.SortQueryAsync(HiddenPath, RtSortOrdersEnum.Descending);
        await h.AddReadingsAsync(1);
        var preference = await h.CreatePreferenceAsync("user-a", onwards: false);

        var tick = await h.TickAsync(preference);

        tick.Sent.Should().BeFalse();
        tick.Failure.Should().BeOfType<RtAttributeHiddenException>().Which.Usage.Should().Be("stored sort order");
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
            // QueryResult has get-only lists, so read the emitted document as JSON.
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
