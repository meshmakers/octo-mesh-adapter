using Meshmakers.Octo.MeshAdapter.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Load;

/// <summary>
/// Creates a web push notification
/// </summary>
/// <param name="next"></param>
[NodeConfiguration(typeof(WebPushNotificationNodeConfiguration))]
public class WebPushNotificationNode(NodeDelegate next) : IPipelineNode
{
    /// <inheritdoc />
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.NodeContext.GetNodeConfiguration<WebPushNotificationNodeConfiguration>();

        await next(dataContext);
    }
}