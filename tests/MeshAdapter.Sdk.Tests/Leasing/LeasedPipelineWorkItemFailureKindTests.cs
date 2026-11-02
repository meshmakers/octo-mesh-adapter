using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Leasing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;

namespace MeshAdapter.Sdk.Tests.Leasing;

/// <summary>
///     AB#8037 — a leased pipeline that fails on a write the database did not acknowledge returns
///     <see cref="PipelineExecutionFailureKind.OutcomeUnknown" /> on its outcome, so the release carries it to the
///     controller (<see cref="LeaseResultDto.FailureKind" />) and a pipeline method answers <c>METHOD_OUTCOME_UNKNOWN</c>.
/// </summary>
public class LeasedPipelineWorkItemFailureKindTests
{
    private const string TenantId = "borrower";

    private readonly IEtlDataOrchestrator _orchestrator = A.Fake<IEtlDataOrchestrator>();
    private readonly LeasedPipelineWorkItem _sut;
    private readonly PipelineConfigurationDto _pipeline;

    public LeasedPipelineWorkItemFailureKindTests()
    {
        var pipelineRtEntityId = new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId());
        _pipeline = new PipelineConfigurationDto(OctoObjectId.GenerateNewId(), pipelineRtEntityId,
            isDebuggingEnabled: false, nodeConfiguration: "{}", configurations: []);

        var registration = new PipelineRegistration(TenantId, OctoObjectId.GenerateNewId(), pipelineRtEntityId, false,
            new NodeDefinitionRoot { Transformations = new List<NodeConfiguration>() }, A.Fake<IGlobalConfiguration>(),
            new Dictionary<string, object?>());

        var registry = A.Fake<IPipelineRegistryService>();
        A.CallTo(() => registry.RegisterPipelinesAsync(TenantId, A<ICollection<PipelineConfigurationDto>>._,
                A<List<DeploymentUpdateErrorMessageDto>>._))
            .Returns(true);
        PipelineRegistration? outRegistration = registration;
        A.CallTo(() => registry.TryGetPipelineRegistration(TenantId, pipelineRtEntityId, out outRegistration!))
            .Returns(true);

        var scope = A.Fake<IAdapterTenantScope>();
        A.CallTo(() => scope.TenantId).Returns(TenantId);

        var etlContext = A.Fake<IMeshEtlContext>();
        A.CallTo(() => etlContext.Properties).Returns(new Dictionary<string, object?>());
        var contextCreator = A.Fake<IContextCreatorService>();
        A.CallTo(() => contextCreator.CreateEtlContext<IMeshEtlContext>(A<PipelineRegistration>._,
                A<ExecutePipelineOptions>._, A<Guid>._))
            .Returns(Task.FromResult(etlContext));

        _sut = new LeasedPipelineWorkItem(new ServiceCollection().BuildServiceProvider(), scope, registry,
            contextCreator, _orchestrator, NullLogger<LeasedPipelineWorkItem>.Instance);
    }

    private LeaseDto ALease() => new()
    {
        LeaseId = "lease-1",
        TenantId = TenantId,
        ExecutionId = Guid.NewGuid().ToString(),
        PipelineRtId = _pipeline.PipelineRtEntityId.RtId.ToString(),
        Pipeline = _pipeline
    };

    [Fact]
    public async Task APipelineThatFailsOnAnUnacknowledgedWrite_ReturnsOutcomeUnknown()
    {
        A.CallTo(() => _orchestrator.ExecutePipelineAsync(A<NodeDefinitionRoot>._, A<IMeshEtlContext>._,
                A<IPipelineDebugger?>._, A<object?>._, A<IPipelineExecutionMode?>._))
            .ThrowsAsync(new InvalidOperationException("Database operation 'ApplyChangesAsync' failed.",
                new MongoDB.Driver.FakeWriteConcernFaultException()));

        var outcome = await _sut.RunAsync(ALease(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(PipelineExecutionFailureKind.OutcomeUnknown, outcome.FailureKind);
        // the message stays free text for people; the kind is the contract
        Assert.Equal("Database operation 'ApplyChangesAsync' failed.", outcome.StatusMessage);
    }

    [Fact]
    public async Task APipelineThatFailsOrdinarily_ReturnsFailed()
    {
        A.CallTo(() => _orchestrator.ExecutePipelineAsync(A<NodeDefinitionRoot>._, A<IMeshEtlContext>._,
                A<IPipelineDebugger?>._, A<object?>._, A<IPipelineExecutionMode?>._))
            .ThrowsAsync(new InvalidOperationException("duplicate key"));

        var outcome = await _sut.RunAsync(ALease(), CancellationToken.None);

        Assert.False(outcome.Success);
        Assert.Equal(PipelineExecutionFailureKind.Failed, outcome.FailureKind);
    }
}
