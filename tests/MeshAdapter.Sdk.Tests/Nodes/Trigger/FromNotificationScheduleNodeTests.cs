using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

public class FromNotificationScheduleNodeTests
{
    private readonly IEventHubControl _eventHubControl = A.Fake<IEventHubControl>();
    private readonly ITriggerContext _triggerContext = A.Fake<ITriggerContext>();
    private Func<NotificationScheduleTick, Task>? _consumer;
    private string? _address;

    public FromNotificationScheduleNodeTests()
    {
        A.CallTo(() => _triggerContext.TenantId).Returns("EcFinal");
        A.CallTo(() => _triggerContext.NodeContext).Returns(A.Fake<INodeContext>());
        A.CallTo(() => _triggerContext.PipelineRtEntityId).Returns(new RtEntityId(
            new RtCkId<CkTypeId>("System.Communication/Pipeline"),
            new OctoObjectId("670000000000000000002581")));

        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(A<string>._,
                A<Func<NotificationScheduleTick, Task>>._))
            .Invokes((string address, Func<NotificationScheduleTick, Task> consumer) =>
            {
                _address = address;
                _consumer = consumer;
            })
            .Returns(A.Fake<EndpointHandle>());
    }

    [Fact]
    public async Task StartAsync_ListensWhereTheControllerSchedulesTheTicks()
    {
        await new FromNotificationScheduleNode(_eventHubControl).StartAsync(_triggerContext);

        Assert.Equal("octo::com-controller::notification-schedule-ecfinal-670000000000000000002581", _address);
    }

    [Fact]
    public async Task Tick_StartsAnExecutionForThePreference()
    {
        object? input = null;
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Invokes((ExecutePipelineOptions _, object? value) => input = value)
            .Returns(Task.FromResult<object?>(null));

        await new FromNotificationScheduleNode(_eventHubControl).StartAsync(_triggerContext);
        await _consumer!(new NotificationScheduleTick("EcFinal", "670000000000000000009999", Guid.NewGuid(),
            DateTime.UtcNow));

        var json = Assert.IsType<JsonObject>(input);
        Assert.Equal("670000000000000000009999", json["preferenceRtId"]!.GetValue<string>());
        Assert.Equal("EcFinal", json["tenantId"]!.GetValue<string>());
    }

    [Fact]
    public async Task Ticks_OfOnePreference_NeverRunAtTheSameTime()
    {
        var release = new TaskCompletionSource();
        var runs = 0;
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .ReturnsLazily(async () =>
            {
                Interlocked.Increment(ref runs);
                await release.Task;
                return null;
            });

        await new FromNotificationScheduleNode(_eventHubControl).StartAsync(_triggerContext);
        var tick = () => _consumer!(new NotificationScheduleTick("EcFinal", "670000000000000000009999", Guid.NewGuid(),
            DateTime.UtcNow));
        var first = tick();
        await Task.WhenAll(Enumerable.Range(0, 5).Select(_ => tick()));
        var other = _consumer!(new NotificationScheduleTick("EcFinal", "670000000000000000008888", Guid.NewGuid(),
            DateTime.UtcNow));
        release.SetResult();
        await Task.WhenAll(first, other);

        Assert.Equal(2, runs);
    }

    [Fact]
    public async Task Tick_FailedExecutionDoesNotEscapeTheConsumer()
    {
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .ThrowsAsync(new InvalidOperationException("boom"));

        await new FromNotificationScheduleNode(_eventHubControl).StartAsync(_triggerContext);
        await _consumer!(new NotificationScheduleTick("EcFinal", "670000000000000000009999", Guid.NewGuid(),
            DateTime.UtcNow));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    public async Task Tick_WithoutPreference_StartsNothingAndDoesNotThrow(string? preferenceRtId)
    {
        await new FromNotificationScheduleNode(_eventHubControl).StartAsync(_triggerContext);
        await _consumer!(new NotificationScheduleTick("EcFinal", preferenceRtId!, Guid.NewGuid(), DateTime.UtcNow));

        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._)).MustNotHaveHappened();
    }
}
