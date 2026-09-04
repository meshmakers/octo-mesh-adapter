using Meshmakers.Octo.MeshAdapter.Nodes.Configuration;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.Extensions.Options;
using Meshmakers.Octo.Sdk.Common.Adapters;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Meshmakers.Octo.Sdk.ServiceClient;
using Meshmakers.Octo.Sdk.Common.Services;
using Meshmakers.Octo.Sdk.MeshAdapter;
using Meshmakers.Octo.Sdk.MeshAdapter.Configuration;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.ExcelImport;
using Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.MeshAdapter.Services;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.CallerBinding;
using Meshmakers.Octo.Sdk.MeshAdapter.Services.HttpRequests;
using Meshmakers.Octo.Sdk.ServiceClient.CommunicationControllerServices;
using Meshmakers.Octo.Services.Notifications.Services;
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
            .RegisterNode<RenderHtmlPdfNode>()
            .RegisterNode<MergePdfNode>()
            .RegisterNode<TransformPdfNode>()
            .RegisterNode<BuildSepaCreditTransferNode>()
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
            .RegisterNode<ImportFromCamt053Node>()
            .RegisterNode<CreateAssociationUpdateNode>()
            .RegisterNode<GetNotificationTemplateNode>()
            .RegisterNode<PlaceholderReplaceNode>()
            .RegisterNode<ResolveNotificationPlaceholdersNode>()
            .RegisterNode<EMailSenderNode>()
            .RegisterNode<EMailSenderNode2>()
            .RegisterNode<SftpUploadNode>()
            .RegisterNode<ToDiscordNode>()
            .RegisterNode<SignalSenderNode>()
            .RegisterNode<GetQueryByIdNode>()
            .RegisterNode<GetStreamDataNode>()
            .RegisterNode<SftpListNode>()
            .RegisterNode<SftpDownloadNode>()
            .RegisterNode<AggregateStreamDataNode>()
            .RegisterNode<GetPipelineConfigByCkTypeIdNode>()
            .RegisterNode<QueryResultToMarkdownTableNode>()
            .RegisterNode<MakeHttpRequestNode>()
            .RegisterNode<RenderDelimitedTextNode>()
            .RegisterNode<GenerateAndStoreReportNode>()
            .RegisterNode<PdfOcrExtractionNode>()
            .RegisterNode<AnthropicAiQueryNode>()
            .RegisterNode<StatisticalAnomalyNode>()
            .RegisterNode<MachineLearningAnomalyNode>()
            .RegisterNode<ReplyToTeamsChannelNode>()
            .RegisterNode<TeamsBotReplyNode>()
            .RegisterNode<SendMicrosoftGraphEmailNode>()
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
            .RegisterTriggerNode<FromHttpRequestNode2>()
            .RegisterTriggerNode<FromPipelineTriggerEventNode>()
            .RegisterTriggerNode<FromSendNotificationNode>()
            .RegisterTriggerNode<FromWatchRtEntityNode>()
            .RegisterTriggerNode<FromTeamsBotNode>()
            .RegisterTriggerNode<FromSignalNode>()
            .RegisterEtlContext<IMeshEtlContext>();

        services.AddSingleton<IHttpRequestService, HttpRequestService>();
        services.AddSingleton<IServiceAccountTokenService, ServiceAccountTokenService>();

        // AB#5126 caller-binding seam. The binder enforces the per-trigger three-state policy for
        // every channel trigger and consumes a single IVerifiedCallerDirectory. That directory is now
        // a COMPOSITE (AB#5123) that dispatches a sender by its kind to per-kind leaf directories
        // (IKindVerifiedCallerDirectory), so the channels coexist:
        //   • EntraID (AB#5124): resolves a Teams sender's AAD object id to the IdP-provisioned user.
        //   • Phone  (AB#5123): resolves a Signal sender's number to the self-service-enrolled user.
        //   • E-mail (AB#5125): resolves an inbound mail's From to the admin-whitelisted user; its
        //     message trust is the DKIM/DMARC verdict the e-mail trigger derived.
        // All read the AB#5122 verified-identifier directory through generic CK access over the
        // tenant repository, and all stay fail-closed for kinds they do not own. A new channel adds
        // one leaf and one registration line with no change to the binder or the composite.
        services.AddSingleton<IEntraIdUserLookup, CkEntraIdUserLookup>();
        services.AddSingleton<IPhoneUserLookup, CkPhoneUserLookup>();
        services.AddSingleton<IEmailUserLookup, CkEmailUserLookup>();
        services.AddSingleton<IKindVerifiedCallerDirectory, EntraIdVerifiedCallerDirectory>();
        services.AddSingleton<IKindVerifiedCallerDirectory, PhoneVerifiedCallerDirectory>();
        services.AddSingleton<IKindVerifiedCallerDirectory, EmailVerifiedCallerDirectory>();
        services.AddSingleton<IVerifiedCallerDirectory, CompositeVerifiedCallerDirectory>();
        services.AddSingleton<IChannelCallerBinder, ChannelCallerBinder>();

        // Shared by every SFTP node. Stateless: the per-server concurrency counters live on
        // the ETL context, so a redeployed pipeline picks up a changed limit.
        services.AddSingleton<ISftpSessionFactory, SshNetSftpSessionFactory>();

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

        // Only the event repository is taken from the notification package; AddOctoNotification()
        // would also install a blueprint bootstrap, the notification and markdown services and
        // replace the runtime engine's audit sink, none of which an adapter needs.
        services.AddScoped<IEventRepository, EventRepository>();
        services.AddSingleton<IAdapterEventService, AdapterEventService>();

        services.AddRuntimeEngine()
            .AddMongoDbRuntimeRepository()
            .AddCrateDbStreamDataRepository<ConfigureStreamDataConfiguration>();

        services.AddOctoServiceInfrastructure();

        services.AddSingleton<IContextCreatorService, MeshContextCreatorService>();
        services.AddScoped<IWellKnownNameLoader, WellKnownNameLoader>();

        // AB#4920: eager CK-model warm-up so the first pipeline execution after a wake from
        // 0 replicas (on-demand lifecycle, AB#4914) does not pay the model load. Triggered by
        // MeshAdapterService.StartupAsync after the configuration was applied — deliberately
        // not a hosted service, because at process start the system context is not configured
        // yet. Background-only, opt-out via AdapterOptions.EagerCkModelLoad.
        services.AddSingleton<ICkModelWarmupService, CkModelWarmupService>();

        // We want to ensure that all mesh adapters are using the same security configuration.
        // Whether the scheme is actually usable cannot be decided here - MeshAdapterConfiguration
        // is bound later - so the decision lives in ConfigureJwtBearerOptions, which leaves
        // JwtBearerOptions.Authority unset when no identity service is configured, and in
        // UseOctoMeshAdapter, which then skips the authentication middleware.
        services.AddCors();
        services.AddAuthentication(JwtBearerDefaults.AuthenticationScheme).AddJwtBearer();
        services.AddSingleton<IConfigureOptions<JwtBearerOptions>, ConfigureJwtBearerOptions>();

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