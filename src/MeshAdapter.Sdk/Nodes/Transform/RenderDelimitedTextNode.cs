using Meshmakers.Octo.MeshAdapter.Nodes.Transform;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Nodes;

namespace Meshmakers.Octo.Sdk.MeshAdapter.Nodes.Transform;

/// <summary>
/// Renders an array of records into one delimited-text document.
/// </summary>
[NodeConfiguration(typeof(RenderDelimitedTextNodeConfiguration))]
// ReSharper disable once ClassNeverInstantiated.Global
public class RenderDelimitedTextNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext, INodeContext nodeContext)
    {
        await next(dataContext, nodeContext);
    }
}
