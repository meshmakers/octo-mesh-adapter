using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshAdapter.Sdk.Tests.Services;

/// <summary>
/// Runs a pipeline through <see cref="MeshAdapterTriggerContext"/> with the REAL SDK
/// orchestrator and a real dry-run-honouring Load node, so the assertion is made where the
/// bug hurt: inside the node. <c>ExecutePipelineOptions.IsDryRun</c> is what the
/// <c>FromExecutePipelineCommand@1</c> trigger sets from an <c>ExecutePipelineRequest</c>;
/// the host context has to turn it into the <c>IPipelineExecutionMode</c> the orchestrator
/// threads onto every node context. AB#5159: the mesh adapter host dropped it, so every
/// node saw a null mode and ran for real. The unit-level contract with the orchestrator
/// is pinned in <see cref="MeshAdapterTriggerContextTests"/>.
/// </summary>
public class MeshAdapterTriggerContextDryRunTests
{
    private const string TenantId = "test-tenant";
    private const string ServerConfiguration = "LkvSftp";
    private const string RemotePath = "/out/AR00001.TXT";

    private readonly ISftpSessionFactory _sessionFactory = A.Fake<ISftpSessionFactory>();
    private readonly DefaultPipelineDebugger _debugger;
    private readonly MeshAdapterTriggerContext _sut;

    public MeshAdapterTriggerContextDryRunTests()
    {
        var pipelineRegistryService = A.Fake<IPipelineRegistryService>();
        var contextCreatorService = A.Fake<IContextCreatorService>();

        var services = new ServiceCollection();
        services.AddLogging();
        // The real orchestrator, node lookup and ETL context accessor, exactly as
        // AddOctoMeshAdapter wires them, reduced to the one node under test.
        services.AddDataPipeline()
            .RegisterNode<SftpDeleteNode>()
            .RegisterEtlContext<IMeshEtlContext>();
        // After AddDataPipeline on purpose: it registers the SDK's own IContextCreatorService,
        // and the last registration is the one GetRequiredService returns.
        services.AddSingleton(pipelineRegistryService);
        services.AddSingleton(contextCreatorService);
        services.AddSingleton(_sessionFactory);
        // The debugger the host resolves when it forces one on for a dry run. Registered as
        // a singleton so the test can read the recorded intents back from the same instance.
        services.AddSingleton<IPipelineDebugger>(sp =>
            new DefaultPipelineDebugger(sp.GetRequiredService<ILoggerFactory>()));
        var serviceProvider = services.BuildServiceProvider();
        _debugger = (DefaultPipelineDebugger)serviceProvider.GetRequiredService<IPipelineDebugger>();

        var pipelineRtEntityId = new RtEntityId("System.Communication/Pipeline", OctoObjectId.GenerateNewId());
        var dataFlowRtId = OctoObjectId.GenerateNewId();
        var globalConfiguration = A.Fake<IGlobalConfiguration>();

        var pipelineRegistration = new PipelineRegistration(
            TenantId,
            dataFlowRtId,
            pipelineRtEntityId,
            false,
            new NodeDefinitionRoot
            {
                Transformations = new List<NodeConfiguration>
                {
                    new SftpDeleteNodeConfiguration
                    {
                        ServerConfiguration = ServerConfiguration,
                        RemotePath = RemotePath
                    }
                }
            },
            globalConfiguration,
            new Dictionary<string, object?>());

        PipelineRegistration? outRegistration = pipelineRegistration;
        A.CallTo(() => pipelineRegistryService.TryGetPipelineRegistration(
                TenantId, pipelineRtEntityId, out outRegistration!))
            .Returns(true);

        // What the node reads off the ETL context: the SFTP server entry.
        var etlContext = A.Fake<IMeshEtlContext>();
        var tenantConfiguration = A.Fake<IGlobalConfiguration>();
        A.CallTo(() => etlContext.GlobalConfiguration).Returns(tenantConfiguration);
        A.CallTo(() => tenantConfiguration.IsDefined(ServerConfiguration)).Returns(true);
        A.CallTo(() => tenantConfiguration.GetValue<SftpServerSettings>(ServerConfiguration))
            .Returns(new SftpServerSettings { Host = "sftp.example.com", Username = "user", Password = "secret" });
        A.CallTo(() => contextCreatorService.CreateEtlContext<IMeshEtlContext>(
                A<PipelineRegistration>._, A<ExecutePipelineOptions>._, A<Guid>._))
            .Returns(Task.FromResult(etlContext));

        _sut = new MeshAdapterTriggerContext(
            serviceProvider, TenantId, dataFlowRtId, pipelineRtEntityId,
            A.Fake<INodeContext>(), globalConfiguration);
    }

    [Fact]
    public async Task DryRun_ReachesLoadNode_SftpDeleteRecordsIntentAndDeletesNothing()
    {
        // Left unconfigured on purpose: a dry run must never open a session.
        var executionId = await _sut.StartExecutePipelineAsync(
            new ExecutePipelineOptions(DateTime.UtcNow) { IsDryRun = true });
        await _sut.EndExecutePipelineAsync(executionId);

        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, A<string>._, A<IMeshEtlContext>._,
            A<INodeContext>._, A<CancellationToken>._)).MustNotHaveHappened();

        // Recorded on the debugger the host forced on: the pipeline itself has debugging off.
        var intentPoint = Assert.Single(_debugger.GetDebugInformation().DebugPoints,
            p => p.DryRunNodeTypeName == DryRunHonouredLoadNodes.SftpDelete);
        var intent = JsonNode.Parse(intentPoint.DryRunIntent!);
        Assert.NotNull(intent);
        Assert.Equal(RemotePath, intent["remotePath"]!.GetValue<string>());
        Assert.Equal("sftp.example.com", intent["host"]!.GetValue<string>());
    }

    [Fact]
    public async Task RealRun_LoadNodeStillDeletes()
    {
        var session = A.Fake<ISftpSession>();
        A.CallTo(() => session.Delete(RemotePath)).Returns(true);
        A.CallTo(() => _sessionFactory.ConnectAsync(A<SftpServerSettings>._, ServerConfiguration,
                A<IMeshEtlContext>._, A<INodeContext>._, A<CancellationToken>._))
            .Returns(session);

        var executionId = await _sut.StartExecutePipelineAsync(new ExecutePipelineOptions(DateTime.UtcNow));
        await _sut.EndExecutePipelineAsync(executionId);

        // The node's two branches are exclusive: a real delete means the dry-run branch,
        // and with it the intent, never happened.
        A.CallTo(() => session.Delete(RemotePath)).MustHaveHappenedOnceExactly();
    }
}
