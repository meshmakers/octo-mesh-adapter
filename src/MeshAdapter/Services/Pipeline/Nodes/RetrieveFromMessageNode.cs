using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

[NodeConfiguration(typeof(RetrieveFromMessageNodeConfiguration))]
internal class RetrieveFromMessageNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        try
        {
            if (etlContext.Message.StartsWith("{"))
            {
                dataContext.Current = JObject.Parse(etlContext.Message);
            }
            else
            {
                dataContext.Current = JArray.Parse(etlContext.Message);
            }
            
        }
        catch (Exception ex)
        {
            dataContext.Logger.Error(dataContext.NodeStack.Peek(), $"Error parsing message: {ex.Message}");
            dataContext.Logger.Debug(dataContext.NodeStack.Peek(), $"Message: {etlContext.Message}");
            return;
        }
        await next(dataContext);
    }
}