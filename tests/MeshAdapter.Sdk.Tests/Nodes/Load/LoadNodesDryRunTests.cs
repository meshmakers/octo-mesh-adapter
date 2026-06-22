using System.Text.Json.Nodes;
using FakeItEasy;
using Meshmakers.Octo.Communication.Contracts.DataTransferObjects;
using Meshmakers.Octo.ConstructionKit.Contracts;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Runtime.Contracts;
using Meshmakers.Octo.Runtime.Contracts.MongoDb;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Repositories;
using Meshmakers.Octo.Runtime.Contracts.RepositoryEntities;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Debugger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Execution;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Microsoft.Extensions.DependencyInjection;

namespace MeshAdapter.Sdk.Tests.Nodes.Load;

/// <summary>
/// Smoke tests pinning the M4-B.2 contract for the SDK-shipped Load nodes:
/// when <see cref="IPipelineExecutionMode.IsDryRun"/> is true, the node MUST
/// suppress its real side effect, record the would-be payload via
/// <see cref="INodeContext.RecordDryRunIntent"/>, and forward to <c>next</c>.
///
/// Per-node payload schema is the contract surface for the agent; key fields
/// are pinned individually so a future contributor's "small cleanup" can't
/// silently drop a field the agent relies on. A representative sink shape
/// from each cluster (MongoDB / CrateDB) is exercised; the catalog pin
/// guards the rest.
/// </summary>
public class LoadNodesDryRunTests
{
    private static readonly IPipelineExecutionMode DryRunOn =
        new DefaultPipelineExecutionMode { IsDryRun = true };

    [Fact]
    public async Task ApplyChangesNode_DryRun_RecordsIntentAndSkipsMongoWrite()
    {
        const string dataPath = "$.updateInfos";
        var recorder = new RecordingDebugger();
        var etlContext = A.Fake<IMeshEtlContext>();
        var tenantRepo = A.Fake<ITenantRepository>();
        A.CallTo(() => etlContext.TenantRepository).Returns(tenantRepo);

        var config = new ApplyChangesNodeConfiguration { Path = dataPath };
        var (dataContext, nodeContext, next) = BuildContext(config, recorder);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(
                new RtCkId<CkTypeId>("Test/Type"),
                new RtEntity(new RtCkId<CkTypeId>("Test/Type"), new OctoObjectId("000000000000000000000001")))
        };
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(dataPath)).Returns(data);

        var node = new ApplyChangesNode(next, etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => tenantRepo.GetSessionAsync()).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();

        var intent = Assert.Single(recorder.Intents,
            i => i.NodeTypeName == DryRunHonouredLoadNodes.ApplyChanges);
        Assert.NotNull(intent.IntentData);
        Assert.Equal(dataPath, intent.IntentData!["path"]!.GetValue<string>());
        Assert.Equal(1, intent.IntentData["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task SaveStreamDataInArchive_DryRun_RecordsIntentAndSkipsCrateDbInsert()
    {
        const string dataPath = "$.data";
        const string archiveRtId = "000000000000000000000099";
        var recorder = new RecordingDebugger();
        var etlContext = A.Fake<IMeshEtlContext>();
        // ISystemContext must never be touched on the dry-run short-circuit; FakeItEasy
        // throws on any unconfigured call, so a non-configured fake catches it.
        var systemContext = A.Fake<ISystemContext>();

        var config = new SaveStreamDataInArchiveNodeConfiguration
        {
            Path = dataPath,
            ArchiveRtId = archiveRtId
        };
        var (dataContext, nodeContext, next) = BuildContext(config, recorder);

        var data = new List<EntityUpdateInfo<RtEntity>>();
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(dataPath)).Returns(data);

        var nodeType = typeof(SaveStreamDataInArchiveNode);
        var ctor = nodeType.GetConstructors().Single();
        var node = (IPipelineNode)ctor.Invoke(new object[] { next, etlContext, systemContext });
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => systemContext.FindTenantContextAsync(A<string>._)).MustNotHaveHappened();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();

        var intent = Assert.Single(recorder.Intents,
            i => i.NodeTypeName == DryRunHonouredLoadNodes.SaveStreamDataInArchive);
        Assert.Equal(archiveRtId, intent.IntentData!["archiveRtId"]!.GetValue<string>());
        Assert.Equal(0, intent.IntentData["count"]!.GetValue<int>());
    }

    [Fact]
    public async Task DryRunOff_ApplyChangesNode_StillWritesToMongo()
    {
        const string dataPath = "$.updateInfos";

        var etlContext = A.Fake<IMeshEtlContext>();
        var tenantRepo = A.Fake<ITenantRepository>();
        var session = A.Fake<IOctoSession>();
        A.CallTo(() => etlContext.TenantRepository).Returns(tenantRepo);
        A.CallTo(() => tenantRepo.GetSessionAsync()).Returns(Task.FromResult(session));

        var config = new ApplyChangesNodeConfiguration { Path = dataPath };
        var (dataContext, nodeContext, next) = BuildContext(config, debugger: null, executionMode: null);

        var data = new List<EntityUpdateInfo<RtEntity>>
        {
            EntityUpdateInfo<RtEntity>.CreateInsert(
                new RtCkId<CkTypeId>("Test/Type"),
                new RtEntity(new RtCkId<CkTypeId>("Test/Type"), new OctoObjectId("000000000000000000000001")))
        };
        A.CallTo(() => dataContext.Get<List<EntityUpdateInfo<RtEntity>>>(dataPath)).Returns(data);

        var node = new ApplyChangesNode(next, etlContext);
        await node.ProcessObjectAsync(dataContext, nodeContext);

        A.CallTo(() => tenantRepo.GetSessionAsync()).MustHaveHappenedOnceOrMore();
        A.CallTo(() => session.StartTransaction()).MustHaveHappenedOnceOrMore();
        A.CallTo(() => next(dataContext, nodeContext)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public void DryRunHonouredLoadNodes_ContainsAllRetrofittedNodeTypes()
    {
        // Pin the catalog set so a deletion is loud, not silent. The MCP tool's
        // LoadNodesNotHonouringDryRun report reads this set.
        Assert.Contains(DryRunHonouredLoadNodes.ApplyChanges, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.ApplyChanges2, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.DeployPipeline, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.SendEMail, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.GrafanaProvisionTenant, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.GrafanaDeprovisionTenant, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.SaveStreamDataInArchive, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.SaveTimeRangeStreamDataInArchive, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.SftpUpload, DryRunHonouredLoadNodes.All);
        Assert.Contains(DryRunHonouredLoadNodes.ToDiscord, DryRunHonouredLoadNodes.All);
        Assert.Equal(10, DryRunHonouredLoadNodes.All.Count);
    }

    /// <summary>
    /// Builds a root-level <see cref="INodeContext"/> bypassing
    /// <see cref="NodeContext.RegisterChildNode(string, uint, INodeConfiguration, IDataContext)"/>
    /// because that path casts the data context to the internal
    /// <c>IDebugSnapshotSource</c> contract — a cast the FakeItEasy proxy can't satisfy. The
    /// Load node under test receives a node context whose
    /// <see cref="INodeContext.PipelineExecutionMode"/> and
    /// <see cref="INodeContext.PipelineDebugger"/> are pre-set, which is the only surface
    /// the dry-run path actually depends on.
    /// </summary>
    private static (IDataContext DataContext, INodeContext NodeContext, NodeDelegate Next)
        BuildContext<TConfig>(TConfig config, IPipelineDebugger? debugger = null,
            IPipelineExecutionMode? executionMode = null)
        where TConfig : class, INodeConfiguration
    {
        executionMode ??= DryRunOn;
        var dataContext = A.Fake<IDataContext>();
        var sp = new ServiceCollection().BuildServiceProvider();
        var nodeContext = NodeContext.CreateRootNodeContext(
            sp,
            new NoOpLogger(),
            typeof(TConfig).Name.Replace("Configuration", ""),
            config,
            debugger,
            debugger == null ? null : executionMode);
        var next = A.Fake<NodeDelegate>();
        return (dataContext, nodeContext, next);
    }

    /// <summary>
    /// In-memory <see cref="IPipelineDebugger"/> stub. Captures RecordDryRunIntent calls
    /// for assertion; LogInput/LogOutput are deliberate no-ops so the production
    /// debug-capture path doesn't crash on a non-snapshot-source FakeItEasy proxy.
    /// </summary>
    private sealed class RecordingDebugger : IPipelineDebugger
    {
        public List<(string NodeTypeName, JsonNode? IntentData)> Intents { get; } = new();

        public IPipelineLogger Logger { get; } = new NoOpLogger();

        public void RegisterPipelineRtEntityId(RtEntityId pipelineRtEntityId, Guid pipelineExecutionId) { }
        public void BeginPipelineExecution() { }
        public Task EndPipelineExecutionAsync() => Task.CompletedTask;
        public void LogInput(string id, NodePath path, string? description, uint sequenceNumber, JsonNode? inputData) { }
        public void LogOutput(string id, NodePath path, string? description, uint sequenceNumber, JsonNode? outputData) { }

        public void RecordDryRunIntent(string id, NodePath path, string? description, uint sequenceNumber,
            string nodeTypeName, JsonNode? intentData)
        {
            Intents.Add((nodeTypeName, intentData));
        }

        public DebugInformationRoot GetDebugInformation() =>
            new() { DebugPoints = new List<DebugPointDto>() };
    }

    private sealed class NoOpLogger : IPipelineLogger
    {
        public void Debug(string nodeId, string nodePath, string message, params object[] args) { }
        public void Info(string nodeId, string nodePath, string message, params object[] args) { }
        public void Warning(string nodeId, string nodePath, string message, params object[] args) { }
        public void Error(string nodeId, string nodePath, string message, params object[] args) { }
        public void Error(string nodeId, string nodePath, Exception exception, string message, params object[] args) { }
    }
}
