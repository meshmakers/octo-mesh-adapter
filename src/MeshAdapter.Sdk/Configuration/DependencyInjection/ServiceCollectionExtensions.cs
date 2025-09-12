using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Aggregations;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Sdk.SimulationNodes;
using Meshmakers.Octo.Services.StreamData.Extensions;

// ReSharper disable once CheckNamespace
namespace Microsoft.Extensions.DependencyInjection;

/// <summary>
///     Extensions for dependency injection's service collection
/// </summary>
public static class ServiceCollectionExtensions
{
    /// <summary>
    ///     Adds infrastructure components to all octo services
    /// </summary>
    /// <param name="services"></param>
    public static IDataPipelineBuilder AddOctoMeshAdapter(this IServiceCollection services)
    {
        // Attention! Sequence of registration is important
        // First, we register the data pipeline nodes and services, afterward we replace
        // some services with our own implementations
        var dataPipelineBuilder = services.AddDataPipeline()
            .AddMeshDataPipelineNodes()
            .AddSimulationNodes()
            .RegisterNode<GetRtEntitiesByWellKnownNameTypeNode>()
            .RegisterNode<GetRtEntitiesByTypeNode>()
            .RegisterNode<GetRtEntitiesByIdNode>()
            .RegisterNode<CreateUpdateInfoNode>()
            .RegisterNode<CreateFileSystemItemUpdateNode>()
            .RegisterNode<ApplyChangesNode>()
            .RegisterNode<ApplyChangesNode2>()
            .RegisterNode<FilterLatestUpdateInfoNode>()
            .RegisterNode<EnrichWithMongoDataNode>()
            .RegisterNode<SaveInTimeSeriesNode>()
            .RegisterNode<GetOrCreateRtEntitiesByTypeNode>()
            .RegisterNode<GetAssociationTargetsNode>()
            .RegisterNode<DataMappingNode>()
            .RegisterNode<ImportFromExcelNode>()
            .RegisterNode<JoinNode>()
            .RegisterNode<MathNode>()
            .RegisterNode<SumAggregationNode>()
            .RegisterNode<CreateAssociationUpdateNode>()
            .RegisterNode<GetNotificationTemplateNode>()
            .RegisterNode<PlaceholderReplaceNode>()
            .RegisterNode<EMailSenderNode>()
            .RegisterNode<GetQueryByIdNode>()
            .RegisterNode<QueryResultToMarkdownTableNode>()
            .RegisterNode<MakeHttpRequestNode>()
            .RegisterNode<GenerateAndStoreReportNode>()
            .RegisterTriggerNode<FromExecutePipelineCommandNode>()
            .RegisterTriggerNode<FromHttpRequestNode>()
            .RegisterTriggerNode<FromPipelineTriggerEventNode>()
            .RegisterTriggerNode<FromSendNotificationNode>()
            .RegisterTriggerNode<FromWatchRtEntityNode>()
            .RegisterEtlContext<IMeshEtlContext>();

        services.AddSingleton<IHttpRequestService, HttpRequestService>();
        services.AddCkModelSystemNotification();

        services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository();

        services.AddStreamDataDatabase<ConfigureStreamDataConfiguration>();

        services.AddOctoServiceInfrastructure();

        services.AddSingleton<IContextCreatorService, MeshContextCreatorService>();
        services.AddScoped<IWellKnownNameLoader, WellKnownNameLoader>();

        // We want to ensure that all mesh adapters are using the same security configuration
        services.AddCors();

        // the MakeHttpRequestNode requires an HttpClient to make requests
        services.AddHttpClient();

        return dataPipelineBuilder;
    }
}