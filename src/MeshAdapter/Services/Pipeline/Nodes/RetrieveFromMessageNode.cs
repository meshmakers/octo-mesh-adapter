using Meshmakers.Octo.Sdk.Common.EtlDataPipeline;
using Meshmakers.Octo.Sdk.Common.EtlDataPipeline.Configuration;
using Newtonsoft.Json.Linq;

namespace Meshmakers.Octo.MeshAdapter.Services.Pipeline.Nodes;

internal class RetrieveFromMessageNodeConfiguration : NodeConfiguration;

[Node("RetrieveFromMessage", 1, typeof(RetrieveFromMessageNodeConfiguration))]
internal class RetrieveFromMessageNode(NodeDelegate next, IMeshEtlContext etlContext) : IPipelineNode
{
    public async Task ProcessObjectAsync(IDataContext dataContext)
    {
        dataContext.Current = JObject.Parse(etlContext.Message);

        await next(dataContext);
    }
}