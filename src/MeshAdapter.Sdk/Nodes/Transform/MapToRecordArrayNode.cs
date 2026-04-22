using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Transforms a JSON object (key/value map) into an array of CK records with two
/// attributes per record — one for the key, one for the value.
/// Suitable for feeding a RecordArray attribute via CreateUpdateInfo@1.
/// </summary>
[NodeConfiguration(typeof(MapToRecordArrayNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class MapToRecordArrayNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<MapToRecordArrayNodeConfiguration>();

        var source = dataContext.Current?.SelectToken(config.Path);
        var records = new JArray();

        if (source is JObject map)
        {
            foreach (var prop in map.Properties())
            {
                if (prop.Value == null || prop.Value.Type == JTokenType.Null) continue;

                var record = new JObject
                {
                    ["CkRecordId"] = new JObject
                    {
                        ["SemanticVersionedFullName"] = config.CkRecordId
                    },
                    ["Attributes"] = new JObject
                    {
                        [config.KeyAttributeName] = prop.Name,
                        [config.ValueAttributeName] = prop.Value
                    }
                };
                records.Add(record);
            }
        }

        dataContext.SetValueByPath(config.TargetPath, config.DocumentMode, config.TargetValueKind,
            config.TargetValueWriteMode, records);

        await next(dataContext, nodeContext);
    }
}
