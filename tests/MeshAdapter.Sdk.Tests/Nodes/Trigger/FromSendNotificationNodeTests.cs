using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

public class FromSendNotificationNodeTests
{
    private readonly IEventHubControl _eventHubControl;
    private readonly ITriggerContext _triggerContext;
    private ExecuteCommandHandler<SendNotificationsRequest>? _capturedHandler;

    public FromSendNotificationNodeTests()
    {
        _eventHubControl = A.Fake<IEventHubControl>();
        _triggerContext = A.Fake<ITriggerContext>();
        var nodeContext = A.Fake<INodeContext>();

        A.CallTo(() => _triggerContext.TenantId).Returns("test-tenant");
        A.CallTo(() => _triggerContext.NodeContext).Returns(nodeContext);

        A.CallTo(() => _eventHubControl.RegisterCommandConsumer(
                A<string>._,
                A<ExecuteCommandHandler<SendNotificationsRequest>>._))
            .Invokes((string _, ExecuteCommandHandler<SendNotificationsRequest> handler) =>
            {
                _capturedHandler = handler;
            })
            .Returns(A.Fake<EndpointHandle>());
    }

    [Fact]
    public async Task StartAsync_RegistersCommandConsumer()
    {
        var node = new FromSendNotificationNode(_eventHubControl);

        await node.StartAsync(_triggerContext);

        A.CallTo(() => _eventHubControl.RegisterCommandConsumer(
                A<string>.That.Contains("test-tenant"),
                A<ExecuteCommandHandler<SendNotificationsRequest>>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task HandleCommand_CallsStartAndEndExecutePipelineAsync()
    {
        var pipelineExecutionId = Guid.NewGuid();
        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(
                A<ExecutePipelineOptions>._, A<object?>._))
            .Returns(pipelineExecutionId);
        A.CallTo(() => _triggerContext.EndExecutePipelineAsync(pipelineExecutionId))
            .Returns(Task.FromResult<object?>(null));

        var node = new FromSendNotificationNode(_eventHubControl);
        await node.StartAsync(_triggerContext);
        Assert.NotNull(_capturedHandler);

        var request = new SendNotificationsRequest("test-tenant");
        ExecuteMeshPipelineResponse? capturedResponse = null;
        Task ResponseFunc(object response)
        {
            capturedResponse = response as ExecuteMeshPipelineResponse;
            return Task.CompletedTask;
        }

        await _capturedHandler(request, ResponseFunc);

        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(
                A<ExecutePipelineOptions>._, A<object?>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _triggerContext.EndExecutePipelineAsync(pipelineExecutionId))
            .MustHaveHappenedOnceExactly();

        Assert.NotNull(capturedResponse);
        Assert.True(capturedResponse.IsSuccessStartingExecution);
        Assert.Equal(pipelineExecutionId, capturedResponse.PipelineExecutionId);
    }

    [Fact]
    public async Task HandleCommand_WhenStartThrows_SendsErrorResponseAndDoesNotCallEnd()
    {
        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(
                A<ExecutePipelineOptions>._, A<object?>._))
            .ThrowsAsync(new InvalidOperationException("Pipeline not found"));

        var node = new FromSendNotificationNode(_eventHubControl);
        await node.StartAsync(_triggerContext);
        Assert.NotNull(_capturedHandler);

        var request = new SendNotificationsRequest("test-tenant");
        ExecuteMeshPipelineResponse? capturedResponse = null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _capturedHandler(request, response =>
            {
                capturedResponse = response as ExecuteMeshPipelineResponse;
                return Task.CompletedTask;
            }));

        Assert.NotNull(capturedResponse);
        Assert.False(capturedResponse.IsSuccessStartingExecution);
        Assert.Contains("Pipeline not found", capturedResponse.ErrorMessage);

        A.CallTo(() => _triggerContext.EndExecutePipelineAsync(A<Guid>._))
            .MustNotHaveHappened();
    }

    [Fact]
    public async Task HandleCommand_SendsResponseBeforeCallingEnd()
    {
        var pipelineExecutionId = Guid.NewGuid();
        var callOrder = new List<string>();

        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(
                A<ExecutePipelineOptions>._, A<object?>._))
            .Returns(pipelineExecutionId);
        A.CallTo(() => _triggerContext.EndExecutePipelineAsync(pipelineExecutionId))
            .Invokes(() => callOrder.Add("EndExecute"))
            .Returns(Task.FromResult<object?>(null));

        var node = new FromSendNotificationNode(_eventHubControl);
        await node.StartAsync(_triggerContext);
        Assert.NotNull(_capturedHandler);

        var request = new SendNotificationsRequest("test-tenant");
        await _capturedHandler(request, _ =>
        {
            callOrder.Add("Response");
            return Task.CompletedTask;
        });

        Assert.Equal(2, callOrder.Count);
        Assert.Equal("Response", callOrder[0]);
        Assert.Equal("EndExecute", callOrder[1]);
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var node = new FromSendNotificationNode(_eventHubControl);

        await node.StopAsync(_triggerContext);
    }
}
