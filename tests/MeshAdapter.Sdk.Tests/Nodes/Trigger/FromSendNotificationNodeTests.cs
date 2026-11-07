using FakeItEasy;
using Meshmakers.Octo.Common.DistributionEventHub.Services;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Contracts.DistributionEventHub.Commands.Payloads;

namespace MeshAdapter.Sdk.Tests.Nodes.Trigger;

public class FromSendNotificationNodeTests
{
    private readonly IEventHubControl _eventHubControl;
    private readonly ITriggerContext _triggerContext;
    private readonly INodeContext _nodeContext;
    private ExecuteCommandHandler<SendNotificationsRequest>? _capturedHandler;

    public FromSendNotificationNodeTests()
    {
        _eventHubControl = A.Fake<IEventHubControl>();
        _triggerContext = A.Fake<ITriggerContext>();
        _nodeContext = A.Fake<INodeContext>();

        A.CallTo(() => _triggerContext.TenantId).Returns("test-tenant");
        A.CallTo(() => _triggerContext.NodeContext).Returns(_nodeContext);

        A.CallTo(() => _eventHubControl.RegisterCommandConsumer(
                A<string>._,
                A<ExecuteCommandHandler<SendNotificationsRequest>>._,
                A<CommandConsumerOptions>._))
            .Invokes((string _, ExecuteCommandHandler<SendNotificationsRequest> handler, CommandConsumerOptions _) =>
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
                A<ExecuteCommandHandler<SendNotificationsRequest>>._,
                A<CommandConsumerOptions>.That.Matches(o => o.ExclusiveByDesignReason != null)))
            .MustHaveHappenedOnceExactly();
        // Never the classic two-argument overload: it logs the AB#6909 deprecation warning.
        A.CallTo(() => _eventHubControl.RegisterCommandConsumer(
                A<string>._,
                A<ExecuteCommandHandler<SendNotificationsRequest>>._))
            .MustNotHaveHappened();
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
        ExecutePipelineResponse? capturedResponse = null;
        Task ResponseFunc(object response)
        {
            capturedResponse = response as ExecutePipelineResponse;
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
        ExecutePipelineResponse? capturedResponse = null;

        await Assert.ThrowsAsync<InvalidOperationException>(async () =>
            await _capturedHandler(request, response =>
            {
                capturedResponse = response as ExecutePipelineResponse;
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
    public async Task HandleCommand_ForeignTenant_DropsMessageWithoutStartingPipeline()
    {
        // AB#8409: the request is published to the type exchange, so this queue also receives other tenants' mails.
        var node = new FromSendNotificationNode(_eventHubControl);
        await node.StartAsync(_triggerContext);
        Assert.NotNull(_capturedHandler);

        var request = new SendNotificationsRequest("other-tenant");
        request.Notifications.Add(new DistNotificationDto("Your code", "code 123456", "victim@example.com", null, null));
        var responded = false;

        // Returns normally (the message is acknowledged), no exception, no response.
        await _capturedHandler(request, _ =>
        {
            responded = true;
            return Task.CompletedTask;
        });

        Assert.False(responded);
        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustNotHaveHappened();
        A.CallTo(() => _triggerContext.EndExecutePipelineAsync(A<Guid>._)).MustNotHaveHappened();
        // WARN with the two tenant ids only, never recipient, subject or body.
        A.CallTo(() => _nodeContext.Warning(A<string>._,
                A<object[]>.That.IsSameSequenceAs(new object[] { "other-tenant", "test-tenant" })))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _nodeContext.Warning(A<string>.That.Matches(m =>
                    m.Contains("victim@example.com") || m.Contains("123456") || m.Contains("Your code")),
                A<object[]>._))
            .MustNotHaveHappened();
    }

    [Theory]
    [InlineData("test-tenant")]
    [InlineData("Test-Tenant")]
    [InlineData("TEST-TENANT")]
    public async Task HandleCommand_OwnTenantAnyCasing_StartsPipeline(string messageTenantId)
    {
        var pipelineExecutionId = Guid.NewGuid();
        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .Returns(pipelineExecutionId);
        A.CallTo(() => _triggerContext.EndExecutePipelineAsync(pipelineExecutionId))
            .Returns(Task.FromResult<object?>(null));

        var node = new FromSendNotificationNode(_eventHubControl);
        await node.StartAsync(_triggerContext);
        Assert.NotNull(_capturedHandler);

        await _capturedHandler(new SendNotificationsRequest(messageTenantId), _ => Task.CompletedTask);

        A.CallTo(() => _triggerContext.StartExecutePipelineAsync(A<ExecutePipelineOptions>._, A<object?>._))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _nodeContext.Warning(A<string>._, A<object[]>._)).MustNotHaveHappened();
    }

    [Fact]
    public async Task StopAsync_WithoutStart_DoesNotThrow()
    {
        var node = new FromSendNotificationNode(_eventHubControl);

        await node.StopAsync(_triggerContext);
    }
}
