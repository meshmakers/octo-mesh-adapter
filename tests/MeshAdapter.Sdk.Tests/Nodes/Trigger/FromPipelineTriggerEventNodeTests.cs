using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Messages;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

public class FromPipelineTriggerEventNodeTests
{
    private readonly IEventHubControl _eventHubControl;
    private readonly ITriggerContext _triggerContext;
    private readonly INodeContext _nodeContext;
    private readonly FromPipelineTriggerEventNodeConfiguration _configuration = new();
    private Func<PipelineTriggerSchedule, RoutedEventDeliveryContext, Task>? _handler;
    private RoutedEventConsumerOptions? _options;

    public FromPipelineTriggerEventNodeTests()
    {
        _eventHubControl = A.Fake<IEventHubControl>();
        _triggerContext = A.Fake<ITriggerContext>();
        _nodeContext = A.Fake<INodeContext>();

        A.CallTo(() => _triggerContext.TenantId).Returns("test-tenant");
        A.CallTo(() => _triggerContext.NodeContext).Returns(_nodeContext);
        A.CallTo(() => _triggerContext.PipelineRtEntityId).Returns(
            new RtEntityId(
                new RtCkId<CkTypeId>("TestModel/Pipeline"),
                new OctoObjectId("000000000000000000000001")));
        A.CallTo(() => _nodeContext.GetNodeConfiguration<FromPipelineTriggerEventNodeConfiguration>())
            .ReturnsLazily(() => _configuration);

        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(
                A<string>._,
                A<Func<PipelineTriggerSchedule, RoutedEventDeliveryContext, Task>>._,
                A<RoutedEventConsumerOptions>._))
            .Invokes((string _, Func<PipelineTriggerSchedule, RoutedEventDeliveryContext, Task> handler,
                RoutedEventConsumerOptions options) =>
            {
                _handler = handler;
                _options = options;
            })
            .Returns(A.Fake<EndpointHandle>());
    }

    [Fact]
    public async Task StartAsync_RegistersRoutedEventConsumer()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(
                A<string>.That.Contains("test-tenant"),
                A<Func<PipelineTriggerSchedule, RoutedEventDeliveryContext, Task>>._,
                A<RoutedEventConsumerOptions>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_UsesCorrectAddress()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        // Must stay in sync with TriggerManagementService.UpdateScheduleAsync in the controller.
        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(
                "octo::bot::pipeline-trigger-test-tenant-000000000000000000000001",
                A<Func<PipelineTriggerSchedule, RoutedEventDeliveryContext, Task>>._,
                A<RoutedEventConsumerOptions>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_ByDefault_RegistersLatestOnly()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        Assert.Same(RoutedEventConsumerOptions.LatestOnly, _options);
        Assert.Equal(1, _options!.PrefetchCount);
        Assert.Equal(1, _options.ConcurrentMessageLimit);
        Assert.True(_options.CoalescePendingMessages);
    }

    [Fact]
    public async Task StartAsync_AllowConcurrentExecution_KeepsLegacyConsumption()
    {
        _configuration.AllowConcurrentExecution = true;
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        Assert.Same(RoutedEventConsumerOptions.Default, _options);
        Assert.False(_options!.CoalescePendingMessages);
    }

    [Fact]
    public void ResolveConsumerOptions_WithoutConfiguration_IsLatestOnly()
    {
        Assert.Same(RoutedEventConsumerOptions.LatestOnly,
            FromPipelineTriggerEventNode.ResolveConsumerOptions(null));
    }

    [Fact]
    public async Task Tick_ExecutesPipelineOnce()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);
        await node.StartAsync(_triggerContext);

        await _handler!(new PipelineTriggerSchedule("test-tenant", Guid.NewGuid(), DateTime.UtcNow),
            RoutedEventDeliveryContext.None);

        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task CoalescedTick_ExecutesOnceAndReportsTheCoalescedCount()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);
        await node.StartAsync(_triggerContext);

        await _handler!(new PipelineTriggerSchedule("test-tenant", Guid.NewGuid(), DateTime.UtcNow),
            new RoutedEventDeliveryContext(19));

        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(_nodeContext)
            .Where(call => call.Method.Name == nameof(INodeContext.Info) &&
                           call.Arguments.OfType<object?[]>().Any(args => args.Any(a => Equals(a, 19L))))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task FailingExecution_IsLoggedAndDoesNotFaultTheMessage()
    {
        A.CallTo(() => _triggerContext.ExecuteAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .ThrowsAsync(new InvalidOperationException("boom"));
        var node = new FromPipelineTriggerEventNode(_eventHubControl);
        await node.StartAsync(_triggerContext);

        await _handler!(new PipelineTriggerSchedule("test-tenant", Guid.NewGuid(), DateTime.UtcNow),
            RoutedEventDeliveryContext.None);

        A.CallTo(_nodeContext)
            .Where(call => call.Method.Name == nameof(INodeContext.Error))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StopAsync(_triggerContext);
    }

    [Fact]
    public async Task StopAsync_AfterStart_DoesNotThrow()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);
        await node.StartAsync(_triggerContext);
        await node.StopAsync(_triggerContext);
    }
}
