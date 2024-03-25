using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.MeshAdapter.Consumers;
using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;

var adapterBuilder = new WebSocketBuilder();

await adapterBuilder.RunAsync(args, builder =>
{
    builder.Services.AddSingleton<IAdapterService, MeshAdapter>();
    builder.Services.AddDataPipeline()
        .RegisterNode<GetRtEntitiesByTypeNode>()
        .RegisterNode<GetRtEntitiesByIdNode>()
        .RegisterNode<CreateUpdateInfoNode>()
        .RegisterNode<ApplyChangesNode>()
        .RegisterNode<RetrieveFromMessageNode>()
        .RegisterNode<EnrichWithMongoDataNode>()
        .RegisterNode<SaveInTimeSeriesNode>()
        .RegisterEtlContext<IMeshEtlContext>();
    
    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();
            
    builder.Services.AddSingleton<IMeshPipelineExecutionService, MeshPipelineExecutionService>();

}, builder => {

    
}, configuration =>
{
    configuration.AddRoutedEventConsumer<PipelineDataReceivedConsumer, PipelineDataReceived>();
    configuration.AddRoutedEventConsumer<PipelineDataSentConsumer, PipelineTriggerSchedule>(QueueNames.PipelineTriggerChannelName);
});