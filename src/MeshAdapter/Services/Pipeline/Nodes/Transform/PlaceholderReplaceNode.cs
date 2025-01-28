using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Transform;

/// <summary>
/// Pipeline node that replaces placeholders in a string
/// </summary>
/// <param name="next">Next node in the pipeline</param>
[NodeConfiguration(typeof(PlaceholderReplaceNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class PlaceholderReplaceNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<PlaceholderReplaceNodeConfiguration>();

        var value = dataContext.GetSimpleValueByPath<string>(c.Path);

        if (string.IsNullOrWhiteSpace(value))
        {
            dataContext.NodeContext.Error("No value found");
            return;
        }

        var replaceRules = c.ReplaceRules;
        foreach (var replaceRule in replaceRules)
        {
            var replace = dataContext.GetSimpleValueByPath<string>(replaceRule.Path);
            value = value.Replace("${" + replaceRule.Placeholder + "}", replace, StringComparison.OrdinalIgnoreCase);
        }

        dataContext.SetValueByPath(c.TargetPath, c.TargetValueKind, c.TargetValueWriteMode, value);

        await next(dataContext);
    }
}