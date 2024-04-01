using Meshmakers.Octo.MeshNodes.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

[NodeConfiguration(typeof(LoggerNodeConfiguration))]
internal class LoggerNode(NodeDelegate next) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        var c = dataContext.GetNodeConfiguration<LoggerNodeConfiguration>();
        dataContext.Logger.Info(dataContext.NodeStack.Peek(), c.Message);

        await next(dataContext);
    }
}