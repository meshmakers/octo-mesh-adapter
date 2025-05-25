using Meshmakers.Octo.MeshAdapter.Nodes.Transform.Aggregations;
using Meshmakers.Octo.Sdk.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform.Aggregations;

/// <summary>
/// Join node that allows joining data from a source with an array of items based on a key.
/// </summary>
/// <param name="next"></param>
[NodeConfiguration(typeof(SumAggregationNodeConfiguration))]
public class SumAggregationNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<SumAggregationNodeConfiguration>();
        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        double d = 0.0;
        foreach (var sumAggregationItem in c.Aggregations)
        {
            var sourceTokens = dataContext.Current.SelectTokens(sumAggregationItem.Path).ToArray();

            foreach (var sourceToken in sourceTokens)
            {
                if (!string.IsNullOrWhiteSpace(sumAggregationItem.FilterPath))
                {
                    var use = sourceToken.SelectTokens(sumAggregationItem.FilterPath)
                        .All(s => s.Value<string?>() == sumAggregationItem.ComparisonValue?.ToString());
                    if (!use)
                    {
                        continue;
                    }
                }

                sourceToken.SelectTokens(sumAggregationItem.AggregationPath).Select(s => s.ToObject<double>())
                    .ToList()
                    .ForEach(v => d += v * sumAggregationItem.Value);
            }
        }

        dataContext.SetValueByPath(c.TargetPath, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode, d);

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }
}