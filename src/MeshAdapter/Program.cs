using Meshmakers.Octo.Communication.Contracts.MessageObjects;
using Meshmakers.Octo.MeshAdapter.Consumers;
using Meshmakers.Octo.MeshAdapter.Repositories;
using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Commands;
using Meshmakers.Octo.Services.Common.DistributionEventHub.Messages;
using Microsoft.AspNetCore.Builder;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
builder.Services.AddRazorPages();

var app = builder.Build();

// Configure the HTTP request pipeline.
if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error");
    // The default HSTS value is 30 days. You may want to change this for production scenarios, see https://aka.ms/aspnetcore-hsts.
    app.UseHsts();
}

app.UseHttpsRedirection();
app.UseStaticFiles();

app.UseRouting();

app.UseAuthorization();

app.MapRazorPages();

app.Run();

var adapterBuilder = new WebSocketBuilder();

await adapterBuilder.RunAsync(args, services =>
{
    services.AddSingleton<IAdapterService, MeshAdapter>();
    services.AddDataPipeline()
        .RegisterNode<GetRtEntitiesByTypeNode>()
        .RegisterNode<GetRtEntitiesByIdNode>()
        .RegisterNode<CreateUpdateInfoNode>()
        .RegisterNode<ApplyChangesNode>()
        .RegisterNode<RetrieveFromMessageNode>()
        .RegisterNode<EnrichWithMongoDataNode>()
        .RegisterEtlContext<IRetrieverEtlContext>();
            
    services.AddTransient<IPipelineConfigurationRepository, PipelineConfigurationRepository>();
    services.AddSingleton<IRetrieverPipelineExecutionService, RetrieverPipelineExecutionService>();
    services.AddSingleton<ISenderPipelineExecutionService, SenderPipelineExecutionService>();
    services.AddSingleton<ISenderManager, SenderManager>();
    services.AddSingleton<IRetrieverManager, RetrieverManager>();

}, builder => {

    
}, configuration =>
{
    configuration.AddRoutedEventConsumer<PipelineDataReceivedConsumer, PipelineDataReceived>();
    configuration.AddRoutedEventConsumer<PipelineDataSentConsumer, PipelineTriggerSchedule>(QueueNames.PipelineTriggerChannelName);
});