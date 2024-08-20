using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.MeshAdapter;
using Meshmakers.Octo.MeshAdapter.Configuration;
using Meshmakers.Octo.MeshAdapter.Consumers;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
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
        .RegisterNode<GetRtEntitiesByTypeNode>()
        .RegisterNode<GetRtEntitiesByIdNode>()
        .RegisterNode<CreateUpdateInfoNode>()
        .RegisterNode<ApplyChangesNode>()
        .RegisterNode<RetrieveFromMessageNode>()
        .RegisterNode<EnrichWithMongoDataNode>()
        .RegisterNode<SaveInTimeSeriesNode>()
        .RegisterNode<LoggerNode>()
        .RegisterEtlContext<IMeshEtlContext>();

    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.Configure<MeshAdapterConfiguration>(options =>
        builder.Configuration.GetSection("Adapter").Bind(options));

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    builder.Services.AddSingleton<IMeshPipelineExecutionService, MeshPipelineExecutionService>();

    builder.Services.AddStreamDataDatabase(configuration =>
    {

        var c = builder.Configuration.GetSection("Adapter").Get<MeshAdapterConfiguration>();
        
        var streamDataHost = c?.StreamDataHost;
        var streamDataUser = c?.StreamDataUser;
        var streamDataPassword = c?.StreamDataPassword;
        
        if (c == null || streamDataHost == null || streamDataUser == null)
        {
            throw MeshAdapterException.StreamDataConfigurationNotFound();
        }

        configuration.ConnectionStringFromConfiguration(
            streamDataHost,
            streamDataUser, 
            streamDataPassword);
    });
}, app => { app.MapObservability(); }, configuration =>
{
    configuration.AddRoutedEventConsumer<PipelineDataReceivedConsumer, PipelineDataReceived>();
    configuration.AddRoutedEventConsumer<PipelineTriggerScheduleConsumer, PipelineTriggerSchedule>(QueueNames
        .PipelineTriggerChannelName);
});