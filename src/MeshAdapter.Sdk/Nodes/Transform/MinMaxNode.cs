using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Finds the item with the minimum or maximum value within an array.
/// Supports int (long), double, and DateTime value types.
/// </summary>
[NodeConfiguration(typeof(MinMaxNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class MinMaxNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<MinMaxNodeConfiguration>();

        var sourceArray = dataContext.GetComplexObjectByPath<List<object>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (sourceArray != null && sourceArray.Any())
        {
            JObject? winningObject = null;
            IComparable? winningValue = null;

            foreach (JObject item in sourceArray.Where(e => e is JObject))
            {
                var token = item.SelectToken(c.ValuePath);
                if (token == null)
                    continue;

                IComparable? comparableValue = token.Type switch
                {
                    JTokenType.Integer => token.Value<double>(),
                    JTokenType.Float => token.Value<double>(),
                    JTokenType.Date => token.Value<DateTime>(),
                    _ => null
                };

                if (comparableValue == null)
                    continue;

                if (winningValue == null)
                {
                    winningValue = comparableValue;
                    winningObject = item;
                }
                else
                {
                    var comparison = comparableValue.CompareTo(winningValue);

                    if ((c.Mode == MinMaxMode.Min && comparison < 0) ||
                        (c.Mode == MinMaxMode.Max && comparison > 0))
                    {
                        winningValue = comparableValue;
                        winningObject = item;
                    }
                }
            }

            if (winningObject != null)
            {
                dataContext.SetValueByPath(c.TargetPath, winningObject, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
            }
        }

        await next(dataContext, nodeContext);
    }
}
