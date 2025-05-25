using Meshmakers.Octo.MeshAdapter.Nodes.PipelineDataTransferObjects;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Join node that allows joining data from a source with an array of items based on a key.
/// </summary>
/// <param name="next"></param>
[NodeConfiguration(typeof(MathNodeConfiguration))]
public class MathNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<MathNodeConfiguration>();
        if (dataContext.Current == null)
        {
            throw MeshAdapterPipelineExecutionException.InputValueNull(nodeContext);
        }

        var sourceTokens = dataContext.Current.SelectTokens(c.Path).ToArray();

        if (!sourceTokens.Any())
        {
            nodeContext.Warning("No source data found at path '{0}'", c.Path);
            return;
        }

        var value = GetValue(dataContext, c);
        if (value == null)
        {
            throw MeshAdapterPipelineExecutionException.ValueNotSet(nodeContext, c.ValuePath);
        }

        foreach (var sourceToken in sourceTokens)
        {
            var sourceValue = sourceToken.SelectToken(c.ItemPath)?.ToObject<double?>();
            if (sourceValue == null)
            {
                nodeContext.Warning("No numeric value found at path '{0}'", c.Path);
                continue;
            }

            var result = c.Operation switch
            {
                MathOperationDto.Add => sourceValue + value,
                MathOperationDto.Subtract => sourceValue - value,
                MathOperationDto.Multiply => sourceValue * value,
                MathOperationDto.Divide => sourceValue / value,
                _ => throw new NotSupportedException($"Operation {c.Operation} is not supported")
            };

            sourceToken.ReplaceNested(c.ItemTargetPath, result);
        }

        await next(dataContext, nodeContext).ConfigureAwait(false);
    }

    private static double? GetValue(IDataContext dataContext,
        MathNodeConfiguration config)
    {
        if (!string.IsNullOrWhiteSpace(config.ValuePath))
        {
            return dataContext.GetSimpleValueByPath<double?>(config.ValuePath);
        }

        return config.Value;
    }
}