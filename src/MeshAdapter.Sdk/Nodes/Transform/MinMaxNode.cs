using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.JsonPath;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

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

        if (dataContext.GetKind(c.Path) == DataKind.Array && dataContext.Length(c.Path) > 0)
        {
            // One detached read context per array item, in document order.
            var items = dataContext.SelectMatches($"{c.Path}[*]").ToList();

            // ValuePath is relative to each item ("value", "metadata.score") or rooted
            // ("$.readings[0].value"); normalize to a rooted form GetValue accepts.
            var valuePath = JsonNodePath.NormalizePathOrRelative(c.ValuePath);

            int? winningIndex = null;
            IComparable? winningValue = null;

            for (var i = 0; i < items.Count; i++)
            {
                // The comparable value boxes through GetValue (shared JsonScalar parity):
                // ISO strings -> DateTime, reals -> double, integers that fit -> int,
                // larger -> long. Normalize all numeric arms to double so comparisons
                // stay uniform across int / long / double.
                var value = items[i].GetValue(valuePath);
                IComparable? comparableValue = value switch
                {
                    DateTime dt => dt,
                    double d => d,
                    long l => (double)l,
                    int i32 => (double)i32,
                    _ => null
                };

                if (comparableValue == null)
                {
                    continue;
                }

                if (winningValue == null)
                {
                    winningValue = comparableValue;
                    winningIndex = i;
                }
                else
                {
                    var comparison = comparableValue.CompareTo(winningValue);

                    if ((c.Mode == MinMaxMode.Min && comparison < 0) ||
                        (c.Mode == MinMaxMode.Max && comparison > 0))
                    {
                        winningValue = comparableValue;
                        winningIndex = i;
                    }
                }
            }

            if (winningIndex != null)
            {
                // The winning item is an arbitrary-shape object; write it whole via its
                // detached context root (dynamic shape — no fixed record applies).
                var winner = items[winningIndex.Value].Get<JsonNode>("$");
                dataContext.Set(c.TargetPath, winner, c.DocumentMode, c.TargetValueKind,
                    c.TargetValueWriteMode);
            }
        }

        await next(dataContext, nodeContext);
    }
}
