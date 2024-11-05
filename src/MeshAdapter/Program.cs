using Meshmakers.Octo.MeshAdapter.Configuration;
using Meshmakers.Octo.MeshAdapter.Middlewares;
using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.MeshAdapter.Services;
using Meshmakers.Octo.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Trigger;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Configuration;
using Meshmakers.Octo.Runtime.Contracts.MongoDb.Extensions;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.Common.Web.Sockets;
using Meshmakers.Octo.Sdk.SimulationNodes;
using Meshmakers.Octo.Services.Common.Cors;
using Meshmakers.Octo.Services.Common.StreamData.Extensions;
using Meshmakers.Octo.Services.Infrastructure.Services;
using Meshmakers.Octo.Services.Observability;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Cors.Infrastructure;
using Microsoft.AspNetCore.Http;

var adapterBuilder = new WebAdapterBuilder();

await adapterBuilder.RunAsync(args, builder =>
{
    builder.AddObservability()
        .AddSystemContextHealthCheck();
    builder.Services.AddSingleton<IAdapterService, MeshAdapterService>();
    builder.Services.AddSingleton<IHttpRequestService, HttpRequestService>();
    builder.Services.AddDataPipeline()
        .AddMeshDataPipelineNodes()
        .AddSimulationNodes()
        .RegisterNode<GetRtEntitiesByTypeNode>()
        .RegisterNode<GetRtEntitiesByIdNode>()
        .RegisterNode<CreateUpdateInfoNode>()
        .RegisterNode<ApplyChangesNode>()
        .RegisterNode<ApplyChangesNode2>()
        .RegisterNode<FilterLatestUpdateInfoNode>()
        .RegisterNode<EnrichWithMongoDataNode>()
        .RegisterNode<SaveInTimeSeriesNode>()
        .RegisterNode<FindOrCreateRtIdNode>()
        .RegisterNode<DataMappingNode>()
        .RegisterNode<ImportFromExcelNode>()
        .RegisterNode<CreateAssociationUpdateNode>()
        .RegisterNode<FindByAssociationNode>()
        .RegisterTriggerNode<FromPipelineDataEventNode>()
        .RegisterTriggerNode<FromPipelineTriggerEventNode>()
        .RegisterTriggerNode<FromExecutePipelineCommandNode>()
        .RegisterTriggerNode<FromHttpRequestNode>()
        .RegisterEtlContext<IMeshEtlContext>();

    builder.Services.Configure<OctoSystemConfiguration>(options =>
        builder.Configuration.GetSection("System").Bind(options));

    builder.Services.Configure<MeshAdapterConfiguration>(options =>
        builder.Configuration.GetSection("Adapter").Bind(options));

    builder.Services.AddRuntimeEngine()
        .AddMongoDbRuntimeRepository();

    builder.Services.AddSingleton<IContextCreatorService, MeshContextCreatorService>();

    builder.Services.AddStreamDataDatabase<ConfigureStreamDataConfiguration>();
    
    builder.Services.AddOctoServiceInfrastructure();
    builder.Services.AddCors();

}, app =>
{
    app.MapObservability();
    app.UseCors();
    app.UseMiddleware<DynamicRouteMiddleware>();

});