using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.MeshAdapter;
using Meshmakers.Octo.MeshAdapter.Configuration;
using Meshmakers.Octo.MeshAdapter.Consumers;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Sdk.SimulationNodes;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Meshmakers.Octo.Services.Common.StreamData.Extensions;
using Meshmakers.Octo.Services.Observability;

var adapterBuilder = new WebAdapterBuilder();

await adapterBuilder.RunAsync(args, builder =>
{
    builder.AddObservability()
        .AddSystemContextHealthCheck();

    builder.Services.AddSingleton<IAdapterService, MeshAdapterService>();
    builder.Services.AddDataPipeline()
        .AddMeshDataPipelineNodes()
        .AddSimulationNodes()
        .RegisterNode<GetRtEntitiesByTypeNode>()
        .RegisterNode<GetRtEntitiesByIdNode>()
        .RegisterNode<CreateUpdateInfoNode>()
        .RegisterNode<ApplyChangesNode>()
        .RegisterNode<FilterLatestUpdateInfoNode>()
        .RegisterNode<RetrieveFromMessageNode>()
        .RegisterNode<EnrichWithMongoDataNode>()
        .RegisterNode<SaveInTimeSeriesNode>()
        .RegisterEtlContext<IMeshEtlContext>();

    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.Configure<MeshAdapterConfiguration>(options =>
        builder.Configuration.GetSection("Adapter").Bind(options));

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    builder.Services.AddSingleton<IMeshPipelineExecutionService, MeshPipelineExecutionService>();

    builder.Services.AddStreamDataDatabase<ConfigureStreamDataConfiguration>();
}, app => { app.MapObservability(); }, configuration =>
{
    configuration.AddCommandConsumer<ExecuteMeshPipelineConsumer, ExecuteMeshPipelineRequest>(QueueNames.ExecuteMeshPipelineCommand);
    configuration.AddRoutedEventConsumer<PipelineDataReceivedConsumer, PipelineDataReceived>();
    configuration.AddRoutedEventConsumer<PipelineTriggerScheduleConsumer, PipelineTriggerSchedule>(QueueNames
        .PipelineTriggerChannelName);
});