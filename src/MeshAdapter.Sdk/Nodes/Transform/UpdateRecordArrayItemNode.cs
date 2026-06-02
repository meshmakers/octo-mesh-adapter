using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

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
        var c = nodeContext.GetNodeConfiguration<UpdateRecordArrayItemNodeConfiguration>();

        if (dataContext.GetKind(c.Path) != DataKind.Array)
        {
            // Log available keys for debugging
            if (dataContext.GetKind("$.key") == DataKind.Object)
            {
                var keys = string.Join(", ", dataContext.Keys("$.key"));
                nodeContext.Warning("No array found at path '{0}'. Available keys on $.key: [{1}]", c.Path, keys);

                // Try to find Attributes
                if (dataContext.GetKind("$.key.Attributes") == DataKind.Object)
                {
                    var attrKeys = string.Join(", ", dataContext.Keys("$.key.Attributes"));
                    nodeContext.Warning("Available attribute keys: [{0}]", attrKeys);
                }
                else if (dataContext.GetKind("$.key.attributes") == DataKind.Object)
                {
                    var attrKeys = string.Join(", ", dataContext.Keys("$.key.attributes"));
                    nodeContext.Warning("Available attribute keys: [{0}]", attrKeys);
                }
            }
            else
            {
                nodeContext.Warning("No array found at path '{0}'", c.Path);
            }

            await next(dataContext, nodeContext);
            return;
        }

        // Resolve the match value
        var matchValue = c.MatchValuePath != null
            ? dataContext.Get<string>(c.MatchValuePath)
            : c.MatchValue;

        if (string.IsNullOrEmpty(matchValue))
        {
            nodeContext.Warning("Match value is null or empty");
            await next(dataContext, nodeContext);
            return;
        }

        var sourceArr = dataContext.Get<JsonArray>(c.Path);
        if (sourceArr is null)
        {
            await next(dataContext, nodeContext);
            return;
        }

        var matchAttrName = c.MatchAttributeName;
        var matchAttrNameLower = c.MatchAttributeName.ToLowerInvariant();

        var updated = new JsonArray();
        var matched = false;

        foreach (var item in sourceArr)
        {
            if (item is JsonObject record)
            {
                var attrs = (record["Attributes"] ?? record["attributes"]) as JsonObject;
                if (attrs is not null && !matched)
                {
                    var attrValueNode = attrs[matchAttrName] ?? attrs[matchAttrNameLower];
                    var attrValue = attrValueNode?.ToString();

                    if (string.Equals(attrValue, matchValue, StringComparison.OrdinalIgnoreCase))
                    {
                        // Deep-clone the matched record and apply attribute updates to the clone
                        var clone = (JsonObject)record.DeepClone();
                        var cloneAttrs = (clone["Attributes"] ?? clone["attributes"]) as JsonObject;
                        if (cloneAttrs is not null)
                        {
                            foreach (var update in c.AttributeUpdates)
                            {
                                JsonNode? newValue;
                                if (update.ValuePath != null)
                                {
                                    newValue = dataContext.Get<JsonNode>(update.ValuePath);
                                }
                                else if (update.Value is string strVal && strVal == "=now()")
                                {
                                    newValue = JsonValue.Create(DateTime.UtcNow.ToString("O"));
                                }
                                else if (update.Value is null)
                                {
                                    newValue = null;
                                }
                                else
                                {
                                    newValue = JsonSerializer.SerializeToNode(update.Value, SystemTextJsonOptions.Default);
                                }

                                cloneAttrs[update.AttributeName] = newValue?.DeepClone();
                            }
                        }
                        updated.Add(clone);
                        matched = true;
                        continue;
                    }
                }
            }

            updated.Add(item?.DeepClone());
        }

        if (!matched)
        {
            // Early-return restored to match the pre-migration Newtonsoft behavior
            // (verified against commit 4cd2a0a). Writing the unchanged cloned array
            // to TargetPath when TargetPath != Path materializes an artifact where
            // one didn't previously exist, breaking downstream readers that relied
            // on absence to signal "no update happened".
            nodeContext.Debug("No record found matching {0}='{1}'", c.MatchAttributeName, matchValue);
            await next(dataContext, nodeContext);
            return;
        }

        // Write modified array to target path
        dataContext.Set(c.TargetPath, updated, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }
}
