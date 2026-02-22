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

public class FromPipelineTriggerEventNodeTests
{
    private readonly IEventHubControl _eventHubControl;
    private readonly ITriggerContext _triggerContext;

    public FromPipelineTriggerEventNodeTests()
    {
        _eventHubControl = A.Fake<IEventHubControl>();
        _triggerContext = A.Fake<ITriggerContext>();
        var nodeContext = A.Fake<INodeContext>();

        A.CallTo(() => _triggerContext.TenantId).Returns("test-tenant");
        A.CallTo(() => _triggerContext.NodeContext).Returns(nodeContext);
        A.CallTo(() => _triggerContext.PipelineRtEntityId).Returns(
            new RtEntityId(
                new RtCkId<CkTypeId>("TestModel/Pipeline"),
                new OctoObjectId("000000000000000000000001")));

        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(
                A<string>._,
                A<Func<PipelineTriggerSchedule, Task>>._))
            .Returns(A.Fake<EndpointHandle>());
    }

    [Fact]
    public async Task StartAsync_RegistersRoutedEventConsumer()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(
                A<string>.That.Contains("test-tenant"),
                A<Func<PipelineTriggerSchedule, Task>>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartAsync_UsesCorrectAddress()
    {
        var node = new FromPipelineTriggerEventNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        A.CallTo(() => _eventHubControl.RegisterRoutedEventConsumer(
                A<string>.That.Contains("000000000000000000000001"),
                A<Func<PipelineTriggerSchedule, Task>>._))
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
