using Meshmakers.Octo.MeshAdapter.Nodes.Nodes;
using Meshmakers.Octo.MeshAdapter.Nodes.Nodes.Load;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes.Extract;

[NodeConfiguration(typeof(RetrieveFromMessageNodeConfiguration))]
internal class RetrieveFromMessageNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        dataContext.Current = JObject.Parse(etlContext.Message);

        await next(dataContext);
    }
}