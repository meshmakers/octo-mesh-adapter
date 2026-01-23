using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Extract;

/// <summary>
/// Node that retrieves all pipeline configurations matching a CkTypeId and writes them as an array
/// </summary>
/// <param name="next">Delegate to the next node in the pipeline</param>
/// <param name="etlContext">The ETL context</param>
[NodeConfiguration(typeof(GetPipelineConfigByCkTypeIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetPipelineConfigByCkTypeIdNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetPipelineConfigByCkTypeIdNodeConfiguration>();

        var rawJsonValues = etlContext.GlobalConfiguration.GetAllRawJsonByCkTypeId(c.CkTypeId);
        var configurationsArray = new JArray(rawJsonValues.Select(JToken.Parse));

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, configurationsArray);

        await next(dataContext, nodeContext);
    }
}
