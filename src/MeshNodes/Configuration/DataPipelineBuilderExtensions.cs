using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
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
        // Register control nodes
        pipelineBuilder.RegisterNodeConfiguration<LoggerNodeConfiguration>();

        // Register load nodes
        pipelineBuilder.RegisterNodeConfiguration<EnrichWithMongoDataConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetRtEntitiesByIdNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<GetRtEntitiesByTypeNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<RetrieveFromMessageNodeConfiguration>();

        
        // Register transform nodes
        pipelineBuilder.RegisterNodeConfiguration<CreateUpdateInfoNodeConfiguration>();

        // Register load nodes
        pipelineBuilder.RegisterNodeConfiguration<ApplyChangesNodeConfiguration>();
        pipelineBuilder.RegisterNodeConfiguration<SaveInTimeSeriesNodeConfiguration>();

        return pipelineBuilder;
    }
}