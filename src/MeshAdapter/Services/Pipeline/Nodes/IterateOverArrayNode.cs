using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

[NodeName("IterateOverArray", 1)]
public class IterateOverArrayNodeConfiguration : NodeConfiguration
{
    public string? TargetPropertyName { get; set; }
    public string? SourcePropertyName { get; set; }
}

/// <summary>
/// Gets rt entities by type
/// </summary>
[NodeConfiguration(typeof(IterateOverArrayNodeConfiguration))]
public class IterateOverArrayNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.GetNodeConfiguration<IterateOverArrayNodeConfiguration>();

        var tokens = dataContext.Current?.SelectTokens(c.SourcePropertyName ?? "$");

        if (tokens != null)
        {
            foreach (var item in tokens)
            {
                if (c.TargetPropertyName != null)
                {
                    dataContext.SetCurrentValueByPath(c.TargetPropertyName, item);
                }
                else
                {
                    dataContext.SetCurrentValue(item);
                }
                
                await next(dataContext);
            }

            return;
        }

        dataContext.Logger.Error(dataContext.NodeStack.Peek(), "Source array not found.");
    }
}