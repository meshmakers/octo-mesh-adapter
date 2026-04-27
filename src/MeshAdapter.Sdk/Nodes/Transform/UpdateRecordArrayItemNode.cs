using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Finds a record in a RecordArray (at the configured Path)
/// by matching a key attribute, updates specified attributes on that record, and writes
/// the modified array to TargetPath.
/// </summary>
[NodeConfiguration(typeof(UpdateRecordArrayItemNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class UpdateRecordArrayItemNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var config = nodeContext.GetNodeConfiguration<UpdateRecordArrayItemNodeConfiguration>();

        // Get the RecordArray from the source path
        var source = dataContext.Current?.SelectToken(config.Path);
        if (source is not JArray recordArray)
        {
            // Log available keys for debugging
            var keyToken = dataContext.Current?.SelectToken("$.key");
            if (keyToken is JObject keyObj)
            {
                var keys = string.Join(", ", keyObj.Properties().Select(p => p.Name));
                nodeContext.Warning("No array found at path '{0}'. Available keys on $.key: [{1}]", config.Path, keys);

                // Try to find Attributes
                var attrsToken = keyObj["Attributes"] ?? keyObj["attributes"];
                if (attrsToken is JObject debugAttrsObj)
                {
                    var attrKeys = string.Join(", ", debugAttrsObj.Properties().Select(p => p.Name));
                    nodeContext.Warning("Available attribute keys: [{0}]", attrKeys);
                }
            }
            else
            {
                nodeContext.Warning("No array found at path '{0}'", config.Path);
            }

            await next(dataContext, nodeContext);
            return;
        }

        // Resolve the match value
        var matchValue = config.MatchValuePath != null
            ? dataContext.GetSimpleValueByPath<string>(config.MatchValuePath)
            : config.MatchValue;

        if (string.IsNullOrEmpty(matchValue))
        {
            nodeContext.Warning("Match value is null or empty");
            await next(dataContext, nodeContext);
            return;
        }

        // Find the matching record
        var matchAttrName = config.MatchAttributeName.ToLowerInvariant();
        JObject? matchedRecord = null;

        foreach (var item in recordArray)
        {
            if (item is not JObject record) continue;

            // Records have an "Attributes" object with attribute names as keys
            var attrs = record["Attributes"] ?? record["attributes"];
            if (attrs == null) continue;

            var attrValue = attrs[config.MatchAttributeName]?.ToString()
                            ?? attrs[matchAttrName]?.ToString();

            if (string.Equals(attrValue, matchValue, StringComparison.OrdinalIgnoreCase))
            {
                matchedRecord = record;
                break;
            }
        }

        if (matchedRecord == null)
        {
            nodeContext.Debug("No record found matching {0}='{1}'", config.MatchAttributeName, matchValue);
            await next(dataContext, nodeContext);
            return;
        }

        // Apply attribute updates to the matched record
        var attrs2 = matchedRecord["Attributes"] ?? matchedRecord["attributes"];
        if (attrs2 is JObject attrsObj)
        {
            foreach (var update in config.AttributeUpdates)
            {
                object? value;
                if (update.ValuePath != null)
                {
                    value = dataContext.GetSimpleValueByPath<object>(update.ValuePath);
                }
                else if (update.Value is string strVal && strVal == "=now()")
                {
                    value = DateTime.UtcNow.ToString("O");
                }
                else
                {
                    value = update.Value;
                }

                attrsObj[update.AttributeName] = value != null ? JToken.FromObject(value) : JValue.CreateNull();
            }
        }

        // Write modified array to target path
        dataContext.SetValueByPath(config.TargetPath, config.DocumentMode, config.TargetValueKind,
            config.TargetValueWriteMode, recordArray);

        await next(dataContext, nodeContext);
    }
}
