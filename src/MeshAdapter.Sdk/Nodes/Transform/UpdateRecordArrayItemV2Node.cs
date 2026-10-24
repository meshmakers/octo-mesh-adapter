using System.Text.Json;
using System.Text.Json.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Updates several records of one RecordArray in a single pass (one read, one write per entity).
/// Each item of the updates array addresses one record by a key; the modified array is written once.
/// Pure in-memory transform: ApplyChanges stays unchanged and sees exactly one update per entity.
/// </summary>
/// <remarks>
/// Concurrency: the node works on the array it was given. It cannot detect that another execution
/// changed the same entity after it was read (see the documentation of the node).
/// </remarks>
[NodeConfiguration(typeof(UpdateRecordArrayItemV2NodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
internal class UpdateRecordArrayItemV2Node(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<UpdateRecordArrayItemV2NodeConfiguration>();

        if (dataContext.GetKind(c.Path) != DataKind.Array)
        {
            nodeContext.Warning("No array found at path '{0}'", c.Path);
            await next(dataContext, nodeContext);
            return;
        }

        if (dataContext.GetKind(c.UpdatesPath) != DataKind.Array)
        {
            nodeContext.Warning("No array of updates found at path '{0}'", c.UpdatesPath);
            await next(dataContext, nodeContext);
            return;
        }

        var sourceArr = dataContext.Get<JsonArray>(c.Path);
        if (sourceArr is null || dataContext.Length(c.UpdatesPath) == 0)
        {
            await next(dataContext, nodeContext);
            return;
        }

        // Working copy; records are modified in place on the copy, the source stays untouched.
        var working = new JsonArray();
        // Match value (case-insensitive) -> attributes of the FIRST record carrying it (as in @1).
        var index = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in sourceArr)
        {
            var clone = item?.DeepClone();
            working.Add(clone);
            if (clone is JsonObject record
                && (record["Attributes"] ?? record["attributes"]) is JsonObject attrs
                && FindKey(attrs, c.MatchAttributeName) is { } key
                && attrs[key]?.ToString() is { Length: > 0 } value)
            {
                index.TryAdd(value, attrs);
            }
        }

        var applied = 0;
        var unmatched = 0;
        var total = 0;

        await dataContext.IterateArrayAsync(c.UpdatesPath, itemContext =>
        {
            total++;
            var matchValue = itemContext.Get<JsonNode>(c.ItemMatchValuePath)?.ToString();
            if (string.IsNullOrEmpty(matchValue) || !index.TryGetValue(matchValue, out var attrs))
            {
                unmatched++;
                return Task.CompletedTask;
            }

            foreach (var update in c.AttributeUpdates)
            {
                var targetKey = FindKey(attrs, update.AttributeName) ?? update.AttributeName;
                attrs[targetKey] = ResolveValue(update, itemContext)?.DeepClone();
            }

            applied++;
            return Task.CompletedTask;
        });

        if (unmatched > 0)
        {
            nodeContext.Warning(
                "UpdateRecordArrayItem@2 skipped {0} of {1} updates without a record matching {2}",
                unmatched, total, c.MatchAttributeName);
        }

        if (applied == 0)
        {
            // Same as @1: do not materialize an unchanged array at TargetPath.
            nodeContext.Debug("No update matched a record of '{0}'", c.Path);
            await next(dataContext, nodeContext);
            return;
        }

        dataContext.Set(c.TargetPath, working, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }

    private static JsonNode? ResolveValue(RecordAttributeUpdate update, IDataContext itemContext)
    {
        if (update.ValuePath != null)
        {
            return itemContext.Get<JsonNode>(update.ValuePath);
        }

        if (update.Value is string strVal && strVal == "=now()")
        {
            return JsonValue.Create(DateTime.UtcNow.ToString("O"));
        }

        return update.Value is null
            ? null
            : JsonSerializer.SerializeToNode(update.Value, SystemTextJsonOptions.Default);
    }

    /// <summary>Finds the stored key of an attribute: as given (PascalCase) or camelCase.</summary>
    private static string? FindKey(JsonObject attrs, string name)
    {
        if (attrs.ContainsKey(name))
        {
            return name;
        }

        var camel = char.ToLowerInvariant(name[0]) + name[1..];
        return attrs.ContainsKey(camel) ? camel : null;
    }
}
