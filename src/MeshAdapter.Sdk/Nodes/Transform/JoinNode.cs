using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Join node that allows joining data from a source with an array of items based on a key.
/// </summary>
/// <param name="next"></param>
[NodeConfiguration(typeof(JoinNodeConfiguration))]
public class JoinNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<JoinNodeConfiguration>();
        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        var sourceTokens = dataContext.Current.SelectTokens(c.Path).ToArray();
        var joinTokens = dataContext.Current.SelectTokens(c.JoinPath).ToArray();

        if (!sourceTokens.Any())
        {
            nodeContext.Warning("No source data found at path '{0}'", c.Path);
            return;
        }
        if (!joinTokens.Any())
        {
            nodeContext.Warning("No join data found at path '{0}'", c.JoinPath);
            return;
        }

        foreach (var sourceToken in sourceTokens)
        {
            var sourceValue = sourceToken.SelectToken(c.KeyPath)?.ToString();
            if (string.IsNullOrEmpty(sourceValue))
            {
                nodeContext.Warning("No value found at key path '{0}' in source data", c.KeyPath);
                continue;
            }

            var joinedItems = joinTokens
                .Where(j => j.SelectToken(c.JoinKeyPath)?.ToString() == sourceValue)
                .ToList();
            var newArray = new JArray(joinedItems);
            sourceToken.ReplaceNested(c.ItemPath, newArray);
        }

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }
}