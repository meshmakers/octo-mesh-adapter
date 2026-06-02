using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Pipeline node that replaces placeholders in a string
/// </summary>
/// <param name="next">Next node in the pipeline</param>
[NodeConfiguration(typeof(PlaceholderReplaceNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class PlaceholderReplaceNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        var c = nodeContext.GetNodeConfiguration<PlaceholderReplaceNodeConfiguration>();

        var value = dataContext.Get<string>(c.Path);

        if (string.IsNullOrWhiteSpace(value))
        {
            nodeContext.Error("No value found");
            return;
        }

        var replaceRules = c.ReplaceRules;
        foreach (var replaceRule in replaceRules)
        {
            var replace = dataContext.Get<string>(replaceRule.Path);
            value = value.Replace("${" + replaceRule.Placeholder + "}", replace, StringComparison.OrdinalIgnoreCase);
        }

        dataContext.Set(c.TargetPath, value, c.DocumentMode, c.TargetValueKind, c.TargetValueWriteMode);

        await next(dataContext, nodeContext);
    }
}