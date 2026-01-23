using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Node that retrieves pipeline configuration by WellKnownName
/// </summary>
/// <param name="next">Delegate to the next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(GetPipelineConfigByWellKnownNameNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetPipelineConfigByWellKnownNameNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetPipelineConfigByWellKnownNameNodeConfiguration>();

        if (!etlContext.GlobalConfiguration.IsDefined(c.WellKnownName))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, nameof(c.WellKnownName), c.WellKnownName);
        }

        var rawJson = etlContext.GlobalConfiguration.GetRawJson(c.WellKnownName);
        var pipelineConfigJson = JToken.Parse(rawJson);

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, pipelineConfigJson);

        await next(dataContext, nodeContext);
    }
}
