using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Extract;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

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

        var ckTypeId = ResolveCkTypeId(c, dataContext, nodeContext);

        var rawJsonValues = etlContext.GlobalConfiguration.GetAllRawJsonByCkTypeId(ckTypeId);
        var configurationsArray = new JsonArray(rawJsonValues.Select(j => JsonNode.Parse(j)).ToArray());

        dataContext.Set(c.TargetPath, configurationsArray, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static string ResolveCkTypeId(GetPipelineConfigByCkTypeIdNodeConfiguration c,
        IDataContext dataContext, INodeContext nodeContext)
    {
        if (c.CkTypeId == null && c.CkTypeIdPath == null)
        {
            throw MeshAdapterPipelineExecutionException.CkTypeIdNotSet(nodeContext);
        }

        if (c.CkTypeId != null)
        {
            return c.CkTypeId;
        }

        var ckTypeIdValue = dataContext.Get<string>(c.CkTypeIdPath!);
        if (ckTypeIdValue == null)
        {
            throw MeshAdapterPipelineExecutionException.CkTypeIdValueNull(nodeContext, c.CkTypeIdPath!);
        }

        return ckTypeIdValue;
    }
}
