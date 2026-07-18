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
            .RegisterNode<RenderDataSheetPdfNode>()
            .RegisterNode<MergePdfNode>()
            .RegisterNode<CreateZipArchiveNode>()
            .RegisterNode<CreateUpdateInfoNode>()
            .RegisterNode<CreateFileSystemItemUpdateNode>()
            .RegisterNode<GetFileSystemContentNode>()
            .RegisterNode<ApplyChangesNode>()
            .RegisterNode<ApplyChangesNode2>()
            .RegisterNode<FilterLatestUpdateInfoNode>()
            .RegisterNode<BackfillFromRtEntityNode>()
            .RegisterNode<SaveStreamDataInArchiveNode>()
            .RegisterNode<SaveTimeRangeStreamDataInArchiveNode>()
            .RegisterNode<UpdateRtEntityIfNewerNode>()
            .RegisterNode<SimulateEnergyMeasurementsNode>()
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
            .RegisterNode<SignalSenderNode>()
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
            .RegisterNode<TeamsBotReplyNode>()
            .RegisterNode<MinMaxNode>()
            .RegisterNode<ApplyDataPointMappingsNode>()
            .RegisterNode<BuildMappingTargetsNode>()
            .RegisterNode<GenerateDataPointMappingsNode>()
            .RegisterNode<ExportDataPointMappingsNode>()
            .RegisterNode<ImportDataPointMappingsNode>()
            .RegisterNode<ValidateDataPointCoverageNode>()
            .RegisterNode<DeployPipelineNode>()
            .RegisterNode<MapToRecordArrayNode>()
            .RegisterNode<UpdateRecordArrayItemNode>()
            .RegisterNode<GrafanaProvisionTenantNode>()
            .RegisterNode<GrafanaDeprovisionTenantNode>()
            .RegisterTriggerNode<FromEmailNode>()
            .RegisterTriggerNode<FromMicrosoftGraphNode>()
            .RegisterTriggerNode<FromMicrosoftGraphEmailNode>()
            .RegisterTriggerNode<FromHttpRequestNode>()
            .RegisterTriggerNode<FromPipelineTriggerEventNode>()
            .RegisterTriggerNode<FromSendNotificationNode>()
            .RegisterTriggerNode<FromWatchRtEntityNode>()
            .RegisterTriggerNode<FromTeamsBotNode>()
            .RegisterTriggerNode<FromSignalNode>()
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
        services.AddHttpClient("Signal");

        // Named HttpClient for OctoMesh MCP server calls — uses a long timeout because
        // some MCP tool calls (e.g. tree queries) can take a while.
        services.AddHttpClient("OctoMcp", c => c.Timeout = TimeSpan.FromMinutes(5));

        // Named HttpClient for Anthropic API — long-running tool-use loops with
        // multiple MCP tool rounds can easily exceed the default 100s HttpClient timeout.
        services.AddHttpClient("Anthropic", c => c.Timeout = TimeSpan.FromMinutes(10));

        return dataPipelineBuilder;
    }
}