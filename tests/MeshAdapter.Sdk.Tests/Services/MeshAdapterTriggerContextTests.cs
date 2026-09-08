using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes.Loads;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services;

public class MeshAdapterTriggerContextTests
{
    private const string TenantId = "test-tenant";

    private readonly IPipelineRegistryService _pipelineRegistryService;
    private readonly IEtlDataOrchestrator _etlDataOrchestrator;
    private readonly IContextCreatorService _contextCreatorService;
    private readonly IPipelineExecutionReporter _executionReporter;
    private readonly IPipelineDebugger _pipelineDebugger;
    private readonly MeshAdapterTriggerContext _sut;
    private readonly RtEntityId _pipelineRtEntityId;
    private readonly PipelineRegistration _pipelineRegistration;

    public MeshAdapterTriggerContextTests()
    {
        _pipelineRegistryService = A.Fake<IPipelineRegistryService>();
        _etlDataOrchestrator = A.Fake<IEtlDataOrchestrator>();
        _contextCreatorService = A.Fake<IContextCreatorService>();
        _executionReporter = A.Fake<IPipelineExecutionReporter>();
        _pipelineDebugger = A.Fake<IPipelineDebugger>();

        var services = new ServiceCollection();
        services.AddLogging(b => b.SetMinimumLevel(LogLevel.Trace));
        services.AddSingleton(_pipelineRegistryService);
        services.AddSingleton(_etlDataOrchestrator);
        services.AddSingleton(_contextCreatorService);
        services.AddSingleton(_executionReporter);
        services.AddSingleton(_pipelineDebugger);
        var serviceProvider = services.BuildServiceProvider();

        _pipelineRtEntityId = new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId());
        var dataFlowRtId = OctoObjectId.GenerateNewId();
        var nodeContext = A.Fake<INodeContext>();
        var globalConfiguration = A.Fake<IGlobalConfiguration>();

        _pipelineRegistration = new PipelineRegistration(
            TenantId,
            dataFlowRtId,
            _pipelineRtEntityId,
            false,
            new NodeDefinitionRoot { Transformations = new List<NodeConfiguration>() },
            globalConfiguration,
            new Dictionary<string, object?>());

        PipelineRegistration? outRegistration = _pipelineRegistration;
        A.CallTo(() => _pipelineRegistryService.TryGetPipelineRegistration(
                TenantId, _pipelineRtEntityId, out outRegistration!))
            .Returns(true);

        _sut = new MeshAdapterTriggerContext(
            serviceProvider, TenantId, dataFlowRtId, _pipelineRtEntityId,
            nodeContext, globalConfiguration);
    }

    [Fact]
    public async Task EndExecutePipelineAsync_ReportsCompletedStatus()
    {
        // Arrange: start a pipeline execution
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties)
            .Returns(new Dictionary<string, object?>());

        A.CallTo(() => _contextCreatorService.CreateEtlContext<IMeshEtlContext>(
                A<PipelineRegistration>._, A<ExecutePipelineOptions>._, A<Guid>._))
            .Returns(Task.FromResult(etlContext));

        A.CallTo(() => _etlDataOrchestrator.ExecutePipelineAsync(
                A<NodeDefinitionRoot>._, A<IMeshEtlContext>._, null, A<object?>._))
            .Returns(Task.FromResult<object?>("result"));

        A.CallTo(() => _executionReporter.ReportExecutionStartAsync(
                A<RtEntityId>._, A<Guid>._, A<PipelineTriggerType>._, A<DateTime>._, A<string?>._))
            .Returns(Task.CompletedTask);

        var executionId = await _sut.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow));

        // Act
        var result = await _sut.EndExecutePipelineAsync(executionId);

        // Assert
        A.CallTo(() => _executionReporter.ReportExecutionEndAsync(
                executionId,
                PipelineExecutionStatus.Completed,
                A<DateTime>._,
                A<int>._,
                null,
                null))
            .MustHaveHappenedOnceExactly();

        Assert.Equal("result", result);
    }

    [Fact]
    public async Task EndExecutePipelineAsync_WithExecutionResult_ReportsOutputData()
    {
        // Arrange
        var properties = new Dictionary<string, object?>
        {
            [SetPipelineExecutionResultNode.ExecutionResultPropertyKey] = "{\"data\":\"test\"}"
        };
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(properties);

        A.CallTo(() => _contextCreatorService.CreateEtlContext<IMeshEtlContext>(
                A<PipelineRegistration>._, A<ExecutePipelineOptions>._, A<Guid>._))
            .Returns(Task.FromResult(etlContext));

        A.CallTo(() => _etlDataOrchestrator.ExecutePipelineAsync(
                A<NodeDefinitionRoot>._, A<IMeshEtlContext>._, null, A<object?>._))
            .Returns(Task.FromResult<object?>(null));

        A.CallTo(() => _executionReporter.ReportExecutionStartAsync(
                A<RtEntityId>._, A<Guid>._, A<PipelineTriggerType>._, A<DateTime>._, A<string?>._))
            .Returns(Task.CompletedTask);

        var executionId = await _sut.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow));

        // Act
        await _sut.EndExecutePipelineAsync(executionId);

        // Assert: output data from etlContext.Properties is passed to reporter
        A.CallTo(() => _executionReporter.ReportExecutionEndAsync(
                executionId,
                PipelineExecutionStatus.Completed,
                A<DateTime>._,
                A<int>._,
                null,
                "{\"data\":\"test\"}"))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task EndExecutePipelineAsync_WhenPipelineFails_ReportsFailedStatus()
    {
        // Arrange
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties)
            .Returns(new Dictionary<string, object?>());

        A.CallTo(() => _contextCreatorService.CreateEtlContext<IMeshEtlContext>(
                A<PipelineRegistration>._, A<ExecutePipelineOptions>._, A<Guid>._))
            .Returns(Task.FromResult(etlContext));

        A.CallTo(() => _etlDataOrchestrator.ExecutePipelineAsync(
                A<NodeDefinitionRoot>._, A<IMeshEtlContext>._, null, A<object?>._))
            .ThrowsAsync(new InvalidOperationException("Pipeline failed"));

        A.CallTo(() => _executionReporter.ReportExecutionStartAsync(
                A<RtEntityId>._, A<Guid>._, A<PipelineTriggerType>._, A<DateTime>._, A<string?>._))
            .Returns(Task.CompletedTask);

        var executionId = await _sut.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow));

        // Act & Assert
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => _sut.EndExecutePipelineAsync(executionId));

        // The reporter should still be called with Failed status
        A.CallTo(() => _executionReporter.ReportExecutionEndAsync(
                executionId,
                PipelineExecutionStatus.Failed,
                A<DateTime>._,
                A<int>._,
                "Pipeline failed",
                null))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartExecutePipelineAsync_DryRun_PassesDryRunModeAndForcesDebuggerOn()
    {
        // Arrange: debugging is off on the registration, so the only debugger the orchestrator
        // can receive is the one the dry run forces on - without one, the intents the Load
        // nodes record have nowhere to go (AB#5159).
        ArrangeEtlContext();

        // Act
        var executionId = await _sut.StartExecutePipelineAsync(
            new ExecutePipelineOptions(DateTime.UtcNow) { IsDryRun = true });
        await _sut.EndExecutePipelineAsync(executionId);

        // Assert
        A.CallTo(() => _etlDataOrchestrator.ExecutePipelineAsync(
                A<NodeDefinitionRoot>._, A<IMeshEtlContext>._, _pipelineDebugger, A<object?>._,
                A<IPipelineExecutionMode>.That.Matches(m => m != null && m.IsDryRun)))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _pipelineDebugger.RegisterPipelineRtEntityId(_pipelineRtEntityId, executionId))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartExecutePipelineAsync_DryRun_WithDebuggingEnabled_KeepsThePipelineDebugger()
    {
        // Arrange: the pipeline's own debugger already captures the intents, so the dry run
        // must pass the mode without resolving and registering a second debugger on top.
        UseRegistrationWithDebuggingEnabled();
        ArrangeEtlContext();

        // Act
        var executionId = await _sut.StartExecutePipelineAsync(
            new ExecutePipelineOptions(DateTime.UtcNow) { IsDryRun = true });
        await _sut.EndExecutePipelineAsync(executionId);

        // Assert
        A.CallTo(() => _etlDataOrchestrator.ExecutePipelineAsync(
                A<NodeDefinitionRoot>._, A<IMeshEtlContext>._, _pipelineDebugger, A<object?>._,
                A<IPipelineExecutionMode>.That.Matches(m => m != null && m.IsDryRun)))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _pipelineDebugger.RegisterPipelineRtEntityId(_pipelineRtEntityId, executionId))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task StartExecutePipelineAsync_RealRun_PassesNoModeAndNoDebugger()
    {
        // Arrange: classic semantics - no mode object at all, and the debugger stays opt-in
        // through the registration's IsDebuggingEnabled.
        ArrangeEtlContext();

        // Act
        var executionId = await _sut.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow));
        await _sut.EndExecutePipelineAsync(executionId);

        // Assert
        A.CallTo(() => _etlDataOrchestrator.ExecutePipelineAsync(
                A<NodeDefinitionRoot>._, A<IMeshEtlContext>._, null, A<object?>._, null))
            .MustHaveHappenedOnceExactly();
        A.CallTo(() => _pipelineDebugger.RegisterPipelineRtEntityId(A<RtEntityId>._, A<Guid>._))
            .MustNotHaveHappened();
    }

    private void ArrangeEtlContext()
    {
        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        A.CallTo(() => _contextCreatorService.CreateEtlContext<IMeshEtlContext>(
                A<PipelineRegistration>._, A<ExecutePipelineOptions>._, A<Guid>._))
            .Returns(Task.FromResult(etlContext));
    }

    private void UseRegistrationWithDebuggingEnabled()
    {
        PipelineRegistration? registration = new PipelineRegistration(
            TenantId,
            _pipelineRegistration.DataFlowRtId,
            _pipelineRtEntityId,
            true,
            _pipelineRegistration.NodeDefinitionRoot,
            _pipelineRegistration.GlobalConfiguration,
            _pipelineRegistration.Dictionary);
        A.CallTo(() => _pipelineRegistryService.TryGetPipelineRegistration(
                TenantId, _pipelineRtEntityId, out registration!))
            .Returns(true);
    }
}
