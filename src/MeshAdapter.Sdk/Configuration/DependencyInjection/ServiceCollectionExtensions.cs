using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Microsoft.Extensions.Options;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Meshmakers.Octo.Sdk.SimulationNodes;

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
            .RegisterNode<CheckDuplicateNode>()
            .RegisterNode<ComputeFileHashNode>()
            .RegisterNode<CreateUpdateInfoNode>()
            .RegisterNode<CreateFileSystemItemUpdateNode>()
            .RegisterNode<ApplyChangesNode>()
            .RegisterNode<ApplyChangesNode2>()
            .RegisterNode<FilterLatestUpdateInfoNode>()
            .RegisterNode<BackfillFromRtEntityNode>()
            .RegisterNode<SaveStreamDataInArchiveNode>()
            .RegisterNode<GetOrCreateRtEntitiesByTypeNode>()
            .RegisterNode<GetAssociationTargetsNode>()
            .RegisterNode<DataMappingNode>()
            .RegisterNode<ImportFromCsvNode>()
            .RegisterNode<ImportFromExcelNode>()
            .RegisterNode<CreateAssociationUpdateNode>()
            .RegisterNode<GetNotificationTemplateNode>()
            .RegisterNode<PlaceholderReplaceNode>()
            .RegisterNode<EMailSenderNode>()
            .RegisterNode<SftpUploadNode>()
            .RegisterNode<ToDiscordNode>()
            .RegisterNode<GetQueryByIdNode>()
            .RegisterNode<GetPipelineConfigByCkTypeIdNode>()
            .RegisterNode<QueryResultToMarkdownTableNode>()
            .RegisterNode<MakeHttpRequestNode>()
            .RegisterNode<GenerateAndStoreReportNode>()
            .RegisterNode<PdfOcrExtractionNode>()
            .RegisterNode<AnthropicAiQueryNode>()
            .RegisterNode<StatisticalAnomalyNode>()
            .RegisterNode<MachineLearningAnomalyNode>()
            .RegisterNode<ReplyToTeamsChannelNode>()
            .RegisterNode<MinMaxNode>()
            .RegisterNode<ApplyDataPointMappingsNode>()
            .RegisterNode<BuildMappingTargetsNode>()
            .RegisterNode<DeployPipelineNode>()
            .RegisterNode<MapToRecordArrayNode>()
            .RegisterNode<UpdateRecordArrayItemNode>()
            .RegisterNode<GrafanaProvisionTenantNode>()
            .RegisterNode<GrafanaDeprovisionTenantNode>()
            .RegisterTriggerNode<FromEmailNode>()
            .RegisterTriggerNode<FromMicrosoftGraphNode>()
            .RegisterTriggerNode<FromHttpRequestNode>()
            .RegisterTriggerNode<FromPipelineTriggerEventNode>()
            .RegisterTriggerNode<FromSendNotificationNode>()
            .RegisterTriggerNode<FromWatchRtEntityNode>()
            .RegisterEtlContext<IMeshEtlContext>();

        services.AddSingleton<IHttpRequestService, HttpRequestService>();
        services.AddSingleton<IServiceAccountTokenService, ServiceAccountTokenService>();

        // Register CommunicationServicesClient for DeployDataFlow node
        services.AddOptions<CommunicationServiceClientOptions>()
            .Configure<IOptions<AdapterOptions>>((options, adapterOptions) =>
            {
                options.EndpointUri = adapterOptions.Value.CommunicationControllerServicesUri;
                options.TenantId = adapterOptions.Value.TenantId;
            });
        services.AddSingleton<ICommunicationServicesClient, CommunicationServicesClient>();
        services.AddSingleton<ICommunicationServiceClientAccessToken>(provider =>
            (ICommunicationServiceClientAccessToken)provider.GetRequiredService<IServiceClientAccessToken>());
        services.AddCkModelSystemNotificationV2();

        services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository()
            .AddCrateDbStreamDataRepository<ConfigureStreamDataConfiguration>();

        services.AddOctoServiceInfrastructure();

        services.AddSingleton<IContextCreatorService, MeshContextCreatorService>();
        services.AddScoped<IWellKnownNameLoader, WellKnownNameLoader>();

        // We want to ensure that all mesh adapters are using the same security configuration
        services.AddCors();

        // the MakeHttpRequestNode requires an HttpClient to make requests
        services.AddHttpClient();
        services.AddHttpClient("Discord");

        return dataPipelineBuilder;
    }
}