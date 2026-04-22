using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.MeshAdapter.Nodes.Trigger;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration.DependencyInjection;
using Microsoft.Extensions.DependencyInjection;

namespace Meshmakers.Octo.MeshAdapter.Nodes.Configuration;

/// <summary>
///     Extension methods for adding Ck model compiler services to the DI container.
/// </summary>
public static class DataPipelineBuilderExtensions
{
    /// <summary>
    ///     Adds Octo Mesh data pipeline serializer services to the specified <see cref="IServiceCollection" />.
    /// </summary>
    /// <param name="pipelineBuilder"></param>
    public static IDataPipelineBuilder AddMeshDataPipelineNodes(this IDataPipelineBuilder pipelineBuilder)
    {
        // Control nodes are for edge and mesh! Implement them in SDK!!

        // Register extract nodes
        pipelineBuilder.RegisterNodeConfiguration<EnrichWithMongoDataConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetAssociationTargetsNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetNotificationTemplateNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetOrCreateRtEntitiesByTypeNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetQueryByIdNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetRtEntitiesByIdNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetRtEntitiesByWellKnownNameNodeConfiguration>();

        // Register load nodes
        pipelineBuilder.RegisterNodeConfiguration<ApplyChangesNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<ApplyChangesNodeConfiguration2>();
        pipelineBuilder.RegisterNodeConfiguration<EMailSenderNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<SftpUploadNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<SaveInTimeSeriesNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GrafanaProvisionTenantNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GrafanaDeprovisionTenantNodeConfiguration>();

        // Register transform nodes
        pipelineBuilder.RegisterNodeConfiguration<QueryResultToMarkdownTableNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<CheckDuplicateNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<ComputeFileHashNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<CreateAssociationUpdateNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<CreateFileSystemUpdateNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<CreateUpdateInfoNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<BuildMappingTargetsNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<MapToRecordArrayNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<DataMappingNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<FilterLatestUpdateInfoNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<ImportFromCsvNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<ImportFromExcelNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<PlaceholderReplaceNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GenerateAndStoreReportNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<MakeHttpRequestNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<MinMaxNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<PdfOcrExtractionNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<AnthropicAiQueryNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<ReplyToTeamsChannelNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<StatisticalAnomalyNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<MachineLearningAnomalyNodeConfiguration>();

        // Register trigger nodes
        pipelineBuilder.RegisterNodeConfiguration<FromEmailNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<FromMicrosoftGraphNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<FromHttpRequestNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<FromPipelineTriggerEventNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<FromSendNotificationNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<FromWatchRtEntityNodeConfiguration>();

        return pipelineBuilder;
    }
}