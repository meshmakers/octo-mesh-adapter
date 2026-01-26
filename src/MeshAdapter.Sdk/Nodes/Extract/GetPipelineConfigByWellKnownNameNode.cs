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

        var wellKnownName = ResolveWellKnownName(c, dataContext, nodeContext);

        if (!etlContext.GlobalConfiguration.IsDefined(wellKnownName))
        {
            throw MeshAdapterPipelineExecutionException.GlobalConfigurationParameterNotFound(
                nodeContext, nameof(c.WellKnownName), wellKnownName);
        }

        var rawJson = etlContext.GlobalConfiguration.GetRawJson(wellKnownName);
        var pipelineConfigJson = JToken.Parse(rawJson);

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, pipelineConfigJson);

        await next(dataContext, nodeContext);
    }

    private static string ResolveWellKnownName(GetPipelineConfigByWellKnownNameNodeConfiguration c,
        IDataContext dataContext, INodeContext nodeContext)
    {
        if (c.WellKnownName == null && c.WellKnownNamePath == null)
        {
            throw MeshAdapterPipelineExecutionException.WellKnownNameNotSet(nodeContext);
        }

        if (c.WellKnownName != null)
        {
            return c.WellKnownName;
        }

        var wellKnownNameValue = dataContext.GetSimpleValueByPath<string>(c.WellKnownNamePath!);
        if (wellKnownNameValue == null)
        {
            throw MeshAdapterPipelineExecutionException.WellKnownNameValueNull(nodeContext, c.WellKnownNamePath!);
        }

        return wellKnownNameValue;
    }
}
