using Meshmakers.Octo.ConstructionKit.Contracts;
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
/// <param name="secretAttributeResolver">Resolves the Secret attributes of the configurations, so their revealed values are masked in diagnostics (AB#5538)</param>
[NodeConfiguration(typeof(GetPipelineConfigByCkTypeIdNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class GetPipelineConfigByCkTypeIdNode(
    NodeDelegate next,
    IMeshEtlContext etlContext,
    IConfigurationSecretAttributeResolver? secretAttributeResolver = null) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<GetPipelineConfigByCkTypeIdNodeConfiguration>();

        var ckTypeId = ResolveCkTypeId(c, dataContext, nodeContext);

        var rawJsonValues = etlContext.GlobalConfiguration.GetAllRawJsonByCkTypeId(ckTypeId);
        var configurationsArray = new JsonArray(rawJsonValues.Select(j => JsonNode.Parse(j)).ToArray());
        // AB#5538: the controller ships Secret values revealed; register them before they enter the
        // data context, so snapshots, logs and persisted results mask them.
        ConfigurationSecrets.Register(nodeContext, secretAttributeResolver, etlContext.TenantId,
            TryParseCkTypeId(ckTypeId), configurationsArray, ckTypeId);

        dataContext.Set(c.TargetPath, configurationsArray, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static RtCkId<CkTypeId>? TryParseCkTypeId(string ckTypeId)
    {
        try
        {
            return ckTypeId;
        }
        catch (Exception)
        {
            // Not a parsable CK type id: ConfigurationSecrets falls back to the known credential names.
            return null;
        }
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
