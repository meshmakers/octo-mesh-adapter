using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Runtime.Contracts.Serialization;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Removes duplicate objects from an array based on a unique property
/// </summary>
[NodeConfiguration(typeof(DistinctNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class DistinctNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<DistinctNodeConfiguration>();

        var sourceArray = dataContext.GetComplexObjectByPath<List<object>>(c.Path,
            RtNewtonsoftSerializer.DefaultSerializer);

        if (sourceArray != null && sourceArray.Any())
        {
            var seenValues = new HashSet<object>();
            var distinctObjects = new List<object>();

            foreach (JObject item in sourceArray.Where(e => e is JObject))
            {
                var token = item.SelectToken(c.DistinctValuePath);
                if (token == null)
                    continue;

                // Convert the token to its appropriate type
                object? uniqueValue = token.Type switch
                {
                    JTokenType.Integer => token.Value<long>(),
                    JTokenType.Float => token.Value<double>(),
                    JTokenType.String => token.Value<string>(),
                    JTokenType.Boolean => token.Value<bool>(),
                    JTokenType.Date => token.Value<DateTime>(),
                    _ => token.ToString()
                };

                if (uniqueValue != null && seenValues.Add(uniqueValue))
                {
                    distinctObjects.Add(item);
                }
            }

            dataContext.SetValueByPath(c.TargetPath, distinctObjects, c.DocumentMode, c.TargetValueKind,
                c.TargetValueWriteMode, RtNewtonsoftSerializer.DefaultSerializer);
        }

        await next(dataContext, nodeContext);
    }
}
